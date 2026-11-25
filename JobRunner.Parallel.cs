using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Mehrere Jobs gleichzeitig: Start nach Messung der Auslastung oder fester Anzahl.
/// </summary>
public sealed partial class JobRunner
{
    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly ParallelMonitor _monitor = ParallelMonitor.Create();
    private readonly SemaphoreSlim _finalizeLock = new(1, 1);
    private string _lastDecision = string.Empty;

    /// <summary>
    /// Gets die Anzahl der Threads, die Jellyfin auf diesem Server nutzen darf.
    /// </summary>
    public int Threads => _monitor.Threads;

    /// <summary>
    /// Stand der parallelen Verarbeitung fuer die Plugin-Seite.
    /// </summary>
    /// <returns>Der Stand.</returns>
    public object ParallelStatus()
    {
        var fixedLimit = Math.Clamp(Config.ParallelJobs, 0, 32);
        return new
        {
            _monitor.Threads,
            Running = _workers.Count,
            Automatic = fixedLimit == 0,
            Limit = fixedLimit == 0 ? _monitor.Cap ?? _monitor.HardLimit : fixedLimit,
            Learned = fixedLimit == 0 && _monitor.Cap is not null,
            Proven = fixedLimit == 0 ? _monitor.Proven : null,
            CpuPercent = _monitor.CpuPercent is double cpu ? Math.Round(cpu) : (double?)null,
            JobThreads = _monitor.JobThreads is double threads ? Math.Round(threads, 1) : (double?)null,
            Decision = _lastDecision
        };
    }

    private static bool IsHardware(string encoder) => !encoder.StartsWith("lib", StringComparison.Ordinal);

    private static double PixelRate(Job job)
        => job.Width > 0 && job.Height > 0 && job.Fps > 0 ? (double)job.Width * job.Height * job.Fps : 1920d * 1080 * 25;

    // Bei mehreren Jobs steht der Name vor jeder Zeile, damit das Live-Protokoll lesbar bleibt.
    private string Tag(Job job) => _workers.Count > 1 ? "[" + job.Name + "] " : string.Empty;

    private bool TryStartWorker(Job job, CancellationToken token)
    {
        var fixedLimit = Math.Clamp(Config.ParallelJobs, 0, 32);
        var running = _workers.Count;
        var decision = _monitor.Decide(
            running,
            fixedLimit,
            IsHardware(job.Encoder),
            _workers.Values.Any(w => IsHardware(w.Job.Encoder)));
        _lastDecision = running == 0 ? string.Empty : decision.Reason;
        if (!decision.Start)
        {
            return false;
        }

        var worker = new Worker(job);
        if (!_workers.TryAdd(job.Id, worker))
        {
            return false;
        }

        if (running > 0)
        {
            _log.Add("PARALLEL: " + job.Name + " startet als " + (running + 1) + ". Job gleichzeitig (" + decision.Reason + ").");
            _logger.LogInformation("Codec Converter: {Name} startet als {Count}. Job gleichzeitig ({Reason})", job.Name, running + 1, decision.Reason);
        }

        _monitor.Started(job.Id, running, fixedLimit == 0);
        worker.Task = Task.Run(() => RunWorkerAsync(worker, token), CancellationToken.None);
        return true;
    }

    private async Task RunWorkerAsync(Worker worker, CancellationToken token)
    {
        try
        {
            await RunJobAsync(worker, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Codec Converter: Job {Name} unerwartet beendet", worker.Job.Name);
        }
        finally
        {
            _workers.TryRemove(worker.Job.Id, out _);
        }
    }

    private void SampleWorkers()
    {
        var samples = _workers.Values
            .Select(w => new WorkerSample(
                w.Job.Id,
                w.CpuSeconds(),
                w.Job.Progress * w.Job.DurationSeconds,
                PixelRate(w.Job),
                w.Paused,
                IsHardware(w.Job.Encoder),
                w.MemoryBytes()))
            .ToList();
        var note = _monitor.Add(samples);
        if (note is not null)
        {
            _log.Add("PARALLEL: " + note);
            _logger.LogInformation("Codec Converter: {Note}", note);
        }

        if (samples.Count == 0)
        {
            _lastDecision = string.Empty;
        }
    }

    private async Task WaitForWorkersAsync(TimeSpan timeout)
    {
        var tasks = _workers.Values.Select(w => w.Task).OfType<Task>().ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
        }
    }

    // Platz, den die anderen laufenden Jobs auf demselben Datentraeger voraussichtlich noch belegen.
    private long ReservedSpace(Job job, DriveInfo drive)
    {
        long reserved = 0;
        foreach (var other in _workers.Values.Select(w => w.Job).Where(j => !string.Equals(j.Id, job.Id, StringComparison.Ordinal)))
        {
            try
            {
                var otherDrive = new DriveInfo(Path.GetDirectoryName(other.TempPath)!);

                // Gleiche Groesse = gleicher Datentraeger (DriveInfo nennt unter Linux den Pfad, nicht den Einhaengepunkt).
                if (otherDrive.TotalSize != drive.TotalSize)
                {
                    continue;
                }

                var written = File.Exists(other.TempPath) ? new FileInfo(other.TempPath).Length : 0;
                reserved += Math.Max(0, (long)(Math.Max(other.EstimatedSize, 0) * 1.5) - written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Nicht ermittelbar: nicht mitzaehlen.
            }
        }

        return reserved;
    }

    /// <summary>
    /// Ein laufender Job mit seinem ffmpeg.
    /// </summary>
    private sealed class Worker
    {
        private double _cpuBase;
        private double _lastCpu;
        private long _lastMemory;

        public Worker(Job job) => Job = job;

        public Job Job { get; }

        public Process? Process { get; set; }

        public Task? Task { get; set; }

        public Stopwatch ActiveWatch { get; } = new();

        public Dictionary<string, string> ProgressBlock { get; } = new(StringComparer.Ordinal);

        public DateTime LastProgressLog { get; set; } = DateTime.MinValue;

        public double? ExpectedSpeed { get; set; }

        public DateTime LastOutput { get; set; } = DateTime.UtcNow;

        public volatile bool CancelRequested;

        public volatile bool Paused;

        // CPU-Zeit aller ffmpeg-Laeufe dieses Jobs (bei NVENC kann es einen zweiten Versuch geben).
        public double CpuSeconds()
        {
            var process = Process;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    _lastCpu = _cpuBase + process.TotalProcessorTime.TotalSeconds;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Gerade beendet: letzter Wert gilt.
            }

            return _lastCpu;
        }

        public long MemoryBytes()
        {
            var process = Process;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Refresh();
                    _lastMemory = process.WorkingSet64;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Gerade beendet.
            }

            return _lastMemory;
        }

        public void ProcessEnded(Process process)
        {
            try
            {
                _cpuBase += process.TotalProcessorTime.TotalSeconds;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                _cpuBase = _lastCpu;
            }

            _lastCpu = _cpuBase;
            Process = null;
        }
    }
}
