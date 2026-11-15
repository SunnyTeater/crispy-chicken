using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CodecConverter.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Ergebnis einer Planung.
/// </summary>
/// <param name="Added">Anzahl der neuen Jobs.</param>
/// <param name="Skipped">Uebersprungene Dateien mit Grund.</param>
public sealed record PlanResult(int Added, IReadOnlyList<string> Skipped);

/// <summary>
/// Arbeitet die Warteschlange ab: immer ein Job gleichzeitig und nur, solange wenig gestreamt wird.
/// </summary>
public sealed class JobRunner : IHostedService, IDisposable
{
    /// <summary>
    /// Hinweis fuer Dateien, die schon umgewandelt sind, in Jellyfins Bibliothek aber noch mit den alten Daten stehen.
    /// </summary>
    public const string PendingRefreshText = "Gerade umgewandelt, Jellyfin liest die Datei noch neu ein";

    private const string TempExtension = ".ccpart";
    private const long BackupReserveBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(60);
    private const string BackupMarker = ".codec-converter-backup";

    private readonly JobStore _store;
    private readonly LibraryIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<JobRunner> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private readonly object _planLock = new();
    private DateTime _lastOutput = DateTime.UtcNow;
    private Task? _loop;
    private Process? _process;
    private string? _currentJobId;
    private string? _cancelJobId;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobRunner"/> class.
    /// </summary>
    /// <param name="store">Die Warteschlange.</param>
    /// <param name="index">Der Bibliotheksindex.</param>
    /// <param name="libraryManager">Der Bibliotheksmanager.</param>
    /// <param name="libraryMonitor">Der Bibliotheksmonitor.</param>
    /// <param name="sessionManager">Der Sitzungsmanager.</param>
    /// <param name="mediaEncoder">Jellyfins ffmpeg-Zugang.</param>
    /// <param name="logger">Der Logger.</param>
    public JobRunner(
        JobStore store,
        LibraryIndex index,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ISessionManager sessionManager,
        IMediaEncoder mediaEncoder,
        ILogger<JobRunner> logger)
    {
        _store = store;
        _index = index;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _sessionManager = sessionManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets die Zahl der Sitzungen, in denen gerade etwas abgespielt wird.
    /// </summary>
    public int ActiveStreams => _sessionManager.Sessions.Count(s => s.NowPlayingItem is not null);

    /// <summary>
    /// Gets die Stream-Grenze: Jobs laufen nur, solange weniger Streams aktiv sind.
    /// </summary>
    public int StreamLimit => Math.Clamp(Config.StreamLimit, 1, 1000);

    /// <summary>
    /// Gets a value indicating whether gerade zu viele Streams laufen.
    /// </summary>
    public bool IsBlocked => ActiveStreams >= StreamLimit;

    /// <summary>
    /// Gets a value indicating whether ein laufender Job angehalten werden kann.
    /// </summary>
    public static bool CanPause => !OperatingSystem.IsWindows();

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Ob die Datei eines Jobs gerade in einer Sitzung abgespielt wird.
    /// </summary>
    private bool IsBeingPlayed(Job job)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is null)
            {
                continue;
            }

            if (session.NowPlayingItem.Id.Equals(job.ItemId)
                || string.Equals(session.FullNowPlayingItem?.Path, job.SourcePath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Prueft, ob Jellyfins ffmpeg einen Encoder kennt.
    /// </summary>
    /// <param name="encoder">Name des Encoders.</param>
    /// <returns>True, wenn vorhanden.</returns>
    public bool SupportsEncoder(string encoder) => _mediaEncoder.SupportsEncoder(encoder);

    /// <summary>
    /// Liefert den Sicherungsordner oder einen Fehlertext.
    /// </summary>
    /// <param name="error">Der Fehlertext.</param>
    /// <returns>Der Ordner oder null.</returns>
    public string? GetBackupRoot(out string? error)
    {
        error = null;
        var configured = (Config.BackupPath ?? string.Empty).Trim();
        if (configured.Length == 0)
        {
            return Path.Combine(_store.DataDirectory, "backup");
        }

        if (!Path.IsPathFullyQualified(configured))
        {
            error = "Der Sicherungsordner muss ein absoluter Pfad sein.";
            return null;
        }

        var root = WithSeparator(Path.GetFullPath(configured));
        foreach (var location in _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations ?? Array.Empty<string>()))
        {
            var library = WithSeparator(Path.GetFullPath(location));
            if (root.StartsWith(library, StringComparison.Ordinal) || library.StartsWith(root, StringComparison.Ordinal))
            {
                // Sonst wuerde Jellyfin die gesicherten Originale als Duplikate in die Bibliothek aufnehmen.
                error = "Der Sicherungsordner darf nicht innerhalb einer Bibliothek liegen (und keine enthalten).";
                return null;
            }
        }

        return Path.GetFullPath(configured);
    }

    /// <summary>
    /// Legt fuer die gewaehlten Dateien Jobs an.
    /// </summary>
    /// <param name="request">Die Anfrage (bereits geprueft).</param>
    /// <param name="target">Der Zielcodec.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <returns>Das Ergebnis.</returns>
    public PlanResult Plan(ConvertRequest request, TargetCodec target, EncoderInfo encoder)
    {
        // Zwei gleichzeitige Anfragen duerfen dieselbe Datei nicht doppelt einplanen.
        lock (_planLock)
        {
            return PlanLocked(request, target, encoder);
        }
    }

    private PlanResult PlanLocked(ConvertRequest request, TargetCodec target, EncoderInfo encoder)
    {
        var skipped = new List<string>();
        var jobs = new List<Job>();
        var open = _store.OpenItemIds();
        var recent = _store.RecentlyConverted();
        var room = JobStore.MaxJobs - _store.Count;

        foreach (var id in request.ItemIds.Distinct())
        {
            var entry = _index.GetEntry(id, out var path);
            if (entry is null || string.IsNullOrEmpty(path))
            {
                skipped.Add("Unbekanntes Element " + id.ToString("N", CultureInfo.InvariantCulture));
                continue;
            }

            string? reason = entry.Blocked;
            if (reason is null && open.Contains(id))
            {
                reason = "bereits in der Warteschlange";
            }

            if (reason is null && recent.TryGetValue(id, out var oldSize) && oldSize == entry.Size)
            {
                reason = PendingRefreshText;
            }

            var container = EncoderCatalog.GetContainer(path, target.Codec);
            var directory = Path.GetDirectoryName(path)!;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var targetPath = Path.Combine(directory, baseName + container.Extension);

            if (reason is null && !string.Equals(targetPath, path, StringComparison.Ordinal) && File.Exists(targetPath))
            {
                reason = "Zieldatei " + Path.GetFileName(targetPath) + " existiert bereits";
            }

            if (reason is null && jobs.Count >= room)
            {
                reason = "Warteschlange ist voll";
            }

            if (reason is not null)
            {
                skipped.Add(entry.FileName + ": " + reason);
                continue;
            }

            var jobId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            jobs.Add(new Job
            {
                Id = jobId,
                ItemId = id,
                Name = DisplayName(entry),
                SourcePath = path,

                // Kein Video-Dateiname: Jellyfin ignoriert die halbfertige Datei beim Scannen.
                TempPath = Path.Combine(directory, baseName + "." + jobId[..16] + TempExtension),
                TargetPath = targetPath,
                SourceCodec = entry.Codec,
                TargetCodec = target.Codec,
                Encoder = encoder.Name,
                Quality = request.Quality,
                Speed = request.Speed,
                SourceSize = entry.Size,
                EstimatedSize = EncoderCatalog.EstimateSize(entry, target, encoder, request.Quality),
                DurationSeconds = entry.DurationSeconds,
                State = JobState.Queued,
                Created = DateTime.UtcNow.AddTicks(jobs.Count)
            });
            open.Add(id);
        }

        if (jobs.Count > 0)
        {
            _store.AddRange(jobs);
        }

        return new PlanResult(jobs.Count, skipped);
    }

    /// <summary>
    /// Bricht einen Job ab oder entfernt einen abgeschlossenen Job aus der Liste.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <returns>Fehlertext oder null.</returns>
    public string? CancelOrRemove(string id)
    {
        var job = _store.Find(id);
        if (job is null)
        {
            return "Job nicht gefunden.";
        }

        lock (_lock)
        {
            switch (job.State)
            {
                case JobState.Queued:
                    // Laeuft der Job in diesem Moment an, erledigt die Abbruch-Markierung den Rest.
                    _cancelJobId = string.Equals(_currentJobId, id, StringComparison.Ordinal) ? id : _cancelJobId;
                    _store.Update(job, j =>
                    {
                        j.State = JobState.Cancelled;
                        j.Finished = DateTime.UtcNow;
                        j.Message = "Abgebrochen";
                    });
                    return null;
                case JobState.Running:
                case JobState.Paused:
                    _cancelJobId = id;
                    return null;
                case JobState.Finalizing:
                    return "Der Job wird gerade abgeschlossen und kann nicht mehr abgebrochen werden.";
                default:
                    _store.Remove(j => string.Equals(j.Id, id, StringComparison.Ordinal));
                    return null;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Recover();
        }
        catch (Exception ex)
        {
            // Ein Fehler hier darf den Serverstart nicht verhindern.
            _logger.LogError(ex, "Codec Converter: Wiederaufnahme nach Neustart fehlgeschlagen");
        }

        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        KillProcess();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        KillProcess();
        _stopping.Dispose();
    }

    private static string DisplayName(MediaEntry entry)
    {
        if (entry.Type == "Episode")
        {
            var number = entry.Season.HasValue && entry.Episode.HasValue
                ? string.Format(CultureInfo.InvariantCulture, " S{0:00}E{1:00}", entry.Season, entry.Episode)
                : string.Empty;
            // Ohne Metadaten wiederholt der Folgenname nur Serie und Nummer.
            var plain = entry.Name.Contains(number.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Name, entry.SeriesName, StringComparison.Ordinal);
            return entry.SeriesName + number + (plain ? string.Empty : " – " + entry.Name);
        }

        // Bei Filmen ist der Dateiname eindeutig, auch wenn es mehrere Versionen gibt.
        return Path.GetFileNameWithoutExtension(entry.FileName);
    }

    private static string WithSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void DeleteIfExists(string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CleanupBackups();

                var job = _store.NextQueued();
                if (job is not null && string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
                {
                    // Kurz nach dem Serverstart hat Jellyfin sein ffmpeg noch nicht geprueft: warten statt scheitern.
                    if (DateTime.UtcNow - _startTime > TimeSpan.FromMinutes(3))
                    {
                        Fail(job, "Jellyfin hat kein ffmpeg gefunden. Bitte den ffmpeg-Pfad in den Wiedergabe-Einstellungen prüfen.");
                    }
                }
                else if (job is not null && !IsBlocked)
                {
                    await RunJobAsync(job, token).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Codec Converter: Fehler in der Warteschlange");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void Fail(Job job, string message)
    {
        _logger.LogWarning("Codec Converter: {Name} fehlgeschlagen: {Message}", job.Name, message);
        _store.Update(job, j =>
        {
            j.State = JobState.Failed;
            j.Message = message;
            j.Finished = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Prueft unmittelbar vor dem Start, ob Quelle und Ziel noch so sind wie bei der Planung.
    /// </summary>
    private string? CheckBeforeStart(Job job)
    {
        var entry = _index.GetEntry(job.ItemId, out var path);
        if (entry is null || !string.Equals(path, job.SourcePath, StringComparison.Ordinal))
        {
            return "Die Datei gehört nicht mehr zur Bibliothek oder wurde verschoben.";
        }

        if (entry.Blocked is not null)
        {
            return entry.Blocked;
        }

        if (_store.RecentlyConverted().TryGetValue(job.ItemId, out var oldSize) && oldSize == entry.Size)
        {
            return "Die Datei wurde gerade erst umgewandelt.";
        }

        var source = new FileInfo(job.SourcePath);
        if (!source.Exists)
        {
            return "Die Quelldatei existiert nicht mehr.";
        }

        if (source.LinkTarget is not null)
        {
            return "Die Quelldatei ist ein Symlink. Symlinks werden nicht umgewandelt.";
        }

        if (!string.Equals(job.TargetPath, job.SourcePath, StringComparison.Ordinal) && File.Exists(job.TargetPath))
        {
            return "Die Zieldatei " + Path.GetFileName(job.TargetPath) + " existiert bereits.";
        }

        var backupRoot = GetBackupRoot(out var backupError);
        if (backupRoot is null)
        {
            return backupError;
        }

        try
        {
            // Jetzt pruefen statt erst nach Stunden des Umwandelns.
            Directory.CreateDirectory(backupRoot);
            var probe = Path.Combine(backupRoot, ".schreibtest-" + job.Id[..8]);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Der Sicherungsordner " + backupRoot + " ist für den Jellyfin-Dienst nicht beschreibbar.";
        }

        try
        {
            var media = new DriveInfo(source.DirectoryName!);
            var needed = (long)(Math.Max(job.EstimatedSize, 0) * 1.1) + (64L * 1024 * 1024);
            if (media.AvailableFreeSpace < needed)
            {
                return "Zu wenig freier Speicher im Medienordner.";
            }

            // Liegt der Sicherungsordner auf einem anderen Datentraeger, wird das Original dorthin kopiert.
            // Ein voller Datentraeger (oft die Systemplatte) wuerde den ganzen Server lahmlegen.
            var backup = new DriveInfo(backupRoot);
            var sameVolume = backup.TotalSize == media.TotalSize && Math.Abs(backup.AvailableFreeSpace - media.AvailableFreeSpace) < 16L * 1024 * 1024;
            if (!sameVolume && backup.AvailableFreeSpace < source.Length + BackupReserveBytes)
            {
                return "Zu wenig freier Speicher im Sicherungsordner " + backupRoot + " (das Original und 2 GB Reserve müssen hineinpassen).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Laesst sich der freie Platz nicht ermitteln, entscheidet ffmpeg (bricht bei vollem Datentraeger ab).
        }

        return null;
    }

    private async Task RunJobAsync(Job job, CancellationToken token)
    {
        lock (_lock)
        {
            if (job.State != JobState.Queued)
            {
                // Zwischen Auswahl und Start abgebrochen.
                return;
            }

            _currentJobId = job.Id;
            _cancelJobId = null;
        }

        try
        {
            var problem = CheckBeforeStart(job);
            if (problem is not null)
            {
                Fail(job, problem);
                return;
            }

            var (_, format) = EncoderCatalog.GetContainer(job.SourcePath, job.TargetCodec);
            List<string> arguments;
            try
            {
                arguments = EncoderCatalog.BuildArguments(job, format, Math.Clamp(Config.Threads, 0, 256), Config.VaapiDevice ?? string.Empty);
            }
            catch (InvalidOperationException ex)
            {
                Fail(job, ex.Message);
                return;
            }

            // Liegt am Zielort der temporaeren Datei schon etwas (Rest oder untergeschobener Link), wird es entfernt;
            // File.Delete loescht einen Link selbst und folgt ihm nicht.
            DeleteIfExists(job.TempPath);
            if (new FileInfo(job.TempPath).LinkTarget is not null)
            {
                File.Delete(job.TempPath);
            }

            _store.Update(job, j =>
            {
                j.State = JobState.Running;
                j.Started = DateTime.UtcNow;
                j.Progress = 0;
                j.Message = null;
            });

            _logger.LogInformation("Codec Converter: starte {Name} ({Source} -> {Target}, {Encoder}, Qualität {Quality})", job.Name, job.SourceCodec, job.TargetCodec, job.Encoder, job.Quality);

            var (exitCode, errorText, cancelled) = await RunFfmpegAsync(job, arguments, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                // Server faehrt herunter: der Job laeuft nach dem Neustart von vorn.
                DeleteIfExists(job.TempPath);
                _store.Update(job, j =>
                {
                    j.State = JobState.Queued;
                    j.Progress = 0;
                });
                return;
            }

            if (cancelled)
            {
                DeleteIfExists(job.TempPath);
                _store.Update(job, j =>
                {
                    j.State = JobState.Cancelled;
                    j.Message = "Abgebrochen";
                    j.Finished = DateTime.UtcNow;
                });
                return;
            }

            if (exitCode != 0)
            {
                DeleteIfExists(job.TempPath);
                Fail(job, "ffmpeg ist mit Code " + exitCode + " gescheitert: " + (errorText.Length == 0 ? "keine Fehlermeldung" : errorText));
                return;
            }

            await FinalizeAsync(job, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Codec Converter: unerwarteter Fehler bei {Name}", job.Name);
            try
            {
                if (job.State != JobState.Finalizing)
                {
                    DeleteIfExists(job.TempPath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanupEx, "Codec Converter: temporäre Datei konnte nicht gelöscht werden");
            }

            Fail(job, ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _currentJobId = null;
                _cancelJobId = null;
                _process = null;
            }
        }
    }

    private async Task<(int ExitCode, string ErrorText, bool Cancelled)> RunFfmpegAsync(Job job, List<string> arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var errors = new StringBuilder();
        var stalled = false;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errors)
            {
                // Nur das Ende der Ausgabe aufheben.
                errors.AppendLine(e.Data);
                if (errors.Length > 4000)
                {
                    errors.Remove(0, errors.Length - 2000);
                }
            }
        };
        process.OutputDataReceived += (_, e) => ReadProgress(job, e.Data);

        process.Start();
        lock (_lock)
        {
            _process = process;
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            // Streams und der Rest des Servers haben Vorrang.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "Codec Converter: Priorität konnte nicht gesenkt werden");
        }

        var paused = false;
        var cancelled = false;
        _lastOutput = DateTime.UtcNow;
        while (!process.HasExited)
        {
            if (!paused && DateTime.UtcNow - _lastOutput > StallTimeout)
            {
                // Ein haengendes ffmpeg wuerde sonst die ganze Warteschlange blockieren.
                _logger.LogWarning("Codec Converter: ffmpeg meldet seit {Minutes} Minuten nichts mehr und wird beendet", StallTimeout.TotalMinutes);
                KillProcess();
                stalled = true;
                break;
            }

            bool cancelRequested;
            lock (_lock)
            {
                cancelRequested = string.Equals(_cancelJobId, job.Id, StringComparison.Ordinal);
            }

            if (token.IsCancellationRequested || cancelRequested)
            {
                cancelled = cancelRequested;
                KillProcess();
                break;
            }

            var blocked = IsBlocked;
            if (blocked && !paused && Signal(process, stop: true))
            {
                paused = true;
                _store.Update(job, j => j.State = JobState.Paused);
                _logger.LogInformation("Codec Converter: {Name} angehalten, {Streams} Streams aktiv", job.Name, ActiveStreams);
            }
            else if (!blocked && paused && Signal(process, stop: false))
            {
                paused = false;
                _lastOutput = DateTime.UtcNow;
                _store.Update(job, j => j.State = JobState.Running);
                _logger.LogInformation("Codec Converter: {Name} läuft weiter", job.Name);
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Zeit fuer die naechste Kontrolle.
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        string errorText;
        lock (errors)
        {
            errorText = errors.ToString().Trim();
        }

        if (errorText.Length > 600)
        {
            errorText = "…" + errorText[^600..];
        }

        if (stalled)
        {
            return (-1, "ffmpeg hat " + StallTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " Minuten lang keinen Fortschritt gemeldet und wurde beendet.", false);
        }

        return (process.ExitCode, errorText, cancelled);
    }

    private void ReadProgress(Job job, string? line)
    {
        _lastOutput = DateTime.UtcNow;
        if (line is null || job.DurationSeconds <= 0)
        {
            return;
        }

        if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
            && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            && microseconds > 0)
        {
            var progress = Math.Clamp(microseconds / 1_000_000d / job.DurationSeconds, 0, 0.999);
            _store.Update(job, j => j.Progress = progress, persist: false);
        }
        else if (line.StartsWith("speed=", StringComparison.Ordinal)
            && double.TryParse(line.AsSpan(6).TrimEnd('x').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            _store.Update(job, j => j.EncodeSpeed = speed, persist: false);
        }
    }

    /// <summary>
    /// Haelt ffmpeg an oder laesst es weiterlaufen (SIGSTOP/SIGCONT). Unter Windows nicht moeglich.
    /// </summary>
    private bool Signal(Process process, bool stop)
    {
        int signal;
        if (OperatingSystem.IsLinux())
        {
            signal = stop ? 19 : 18;
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            signal = stop ? 17 : 19;
        }
        else
        {
            return false;
        }

        try
        {
            return !process.HasExited && NativeMethods.Kill(process.Id, signal) == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Codec Converter: ffmpeg konnte nicht angehalten bzw. fortgesetzt werden");
            return false;
        }
    }

    private void KillProcess()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
        }

        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "Codec Converter: ffmpeg war bereits beendet");
        }
    }

    private async Task<MediaInfo?> ProbeAsync(string path, CancellationToken token)
    {
        try
        {
            return await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File },
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Codec Converter: {Path} konnte nicht geprüft werden", path);
            return null;
        }
    }

    /// <summary>
    /// Vergleicht die neue Datei mit dem Original. Liefert einen Fehlertext oder null.
    /// </summary>
    private async Task<string?> VerifyAsync(Job job, CancellationToken token)
    {
        var temp = new FileInfo(job.TempPath);
        if (!temp.Exists || temp.Length < 1024)
        {
            return "ffmpeg hat keine verwertbare Datei erzeugt.";
        }

        if (temp.LinkTarget is not null)
        {
            return "Die temporäre Datei ist ein Symlink.";
        }

        var result = await ProbeAsync(job.TempPath, token).ConfigureAwait(false);
        var video = result?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (result is null || video is null)
        {
            return "Die neue Datei enthält keine lesbare Videospur.";
        }

        if (!string.Equals(video.Codec, job.TargetCodec, StringComparison.OrdinalIgnoreCase))
        {
            return "Die neue Datei hat den Codec " + video.Codec + " statt " + job.TargetCodec + ".";
        }

        var original = await ProbeAsync(job.SourcePath, token).ConfigureAwait(false);
        var expected = original?.RunTimeTicks is > 0 ? TimeSpan.FromTicks(original.RunTimeTicks.Value).TotalSeconds : job.DurationSeconds;
        var actual = result.RunTimeTicks is > 0 ? TimeSpan.FromTicks(result.RunTimeTicks.Value).TotalSeconds : 0;
        if (Math.Abs(expected - actual) > Math.Max(2, expected * 0.01))
        {
            return string.Format(CultureInfo.InvariantCulture, "Die Laufzeit weicht ab ({0:0} s statt {1:0} s).", actual, expected);
        }

        if (original?.MediaStreams is not null)
        {
            foreach (var type in new[] { MediaStreamType.Audio, MediaStreamType.Subtitle })
            {
                var before = original.MediaStreams.Count(s => s.Type == type && !s.IsExternal);
                var after = result.MediaStreams!.Count(s => s.Type == type && !s.IsExternal);
                if (before != after)
                {
                    return (type == MediaStreamType.Audio ? "Tonspuren" : "Untertitelspuren") + " fehlen in der neuen Datei (" + after + " statt " + before + ").";
                }
            }
        }

        return null;
    }

    private async Task FinalizeAsync(Job job, CancellationToken token)
    {
        _store.Update(job, j =>
        {
            j.State = JobState.Finalizing;
            j.Progress = 1;
        });

        var problem = await VerifyAsync(job, token).ConfigureAwait(false);
        if (problem is not null)
        {
            DeleteIfExists(job.TempPath);
            Fail(job, problem + " Das Original wurde nicht angetastet.");
            return;
        }

        var root = GetBackupRoot(out var backupError);
        if (root is null)
        {
            DeleteIfExists(job.TempPath);
            Fail(job, backupError + " Das Original wurde nicht angetastet.");
            return;
        }

        // Wird genau diese Datei gerade abgespielt, bliebe die Wiedergabe beim Austausch haengen: warten, bis sie endet.
        var waiting = false;
        while (IsBeingPlayed(job))
        {
            if (!waiting)
            {
                waiting = true;
                _store.Update(job, j => j.Message = "Fertig umgewandelt. Wartet mit dem Austausch, bis die Wiedergabe dieser Datei endet.");
                _logger.LogInformation("Codec Converter: {Name} wird gerade abgespielt, Austausch wartet", job.Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Server faehrt herunter: nichts halb Fertiges zuruecklassen, der Job laeuft spaeter erneut.
                DeleteIfExists(job.TempPath);
                _store.Update(job, j =>
                {
                    j.State = JobState.Queued;
                    j.Progress = 0;
                    j.Message = null;
                });
                return;
            }
        }

        var backupDirectory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + job.Id[..8]);
        var backupFile = Path.Combine(backupDirectory, Path.GetFileName(job.SourcePath));
        var originalMoved = false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(backupDirectory);
            }
            else
            {
                // Nur der Jellyfin-Dienst darf an die gesicherten Originale, auch wenn der Sicherungsordner selbst offener ist.
                Directory.CreateDirectory(backupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(Path.Combine(backupDirectory, BackupMarker), "Original von: " + job.SourcePath + Environment.NewLine);

            if (!OperatingSystem.IsWindows())
            {
                // Die neue Datei bekommt dieselben Zugriffsrechte wie das Original.
                File.SetUnixFileMode(job.TempPath, File.GetUnixFileMode(job.SourcePath));
            }

            // Vor dem Verschieben vermerken, wohin das Original geht: nach einem Absturz laesst sich der Stand rekonstruieren.
            _store.Update(job, j => j.BackupFile = backupFile);

            File.Move(job.SourcePath, backupFile, false);
            originalMoved = true;

            File.Move(job.TempPath, job.TargetPath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(job, RollBack(job, backupFile, backupDirectory, originalMoved, ex));
            return;
        }

        var size = new FileInfo(job.TargetPath).Length;
        _store.Update(job, j =>
        {
            j.State = JobState.Done;
            j.ResultSize = size;
            j.Finished = DateTime.UtcNow;
            j.Message = "Original gesichert unter " + backupFile;
        });
        _logger.LogInformation("Codec Converter: {Name} fertig, {Before} -> {After} Bytes", job.Name, job.SourceSize, size);

        RefreshLibrary(job);
    }

    /// <summary>
    /// Stellt nach einem Fehler beim Abschluss den Ausgangszustand wieder her.
    /// </summary>
    private string RollBack(Job job, string backupFile, string backupDirectory, bool originalMoved, Exception cause)
    {
        try
        {
            if (originalMoved)
            {
                File.Move(backupFile, job.SourcePath, false);
            }
            else if (File.Exists(job.SourcePath))
            {
                // Abgebrochene Kopie auf einen anderen Datentraeger.
                DeleteIfExists(backupFile);
            }

            DeleteIfExists(job.TempPath);
            if (Directory.Exists(backupDirectory) && !File.Exists(backupFile))
            {
                Directory.Delete(backupDirectory, true);
            }

            _store.Update(job, j => j.BackupFile = null);
            return "Abschluss fehlgeschlagen: " + cause.Message + " Das Original liegt unverändert an seinem Platz.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Codec Converter: Zurückrollen fehlgeschlagen für {Name}", job.Name);
            return "Abschluss fehlgeschlagen: " + cause.Message + " Bitte von Hand prüfen. Original: " + (File.Exists(backupFile) ? backupFile : job.SourcePath) + ", neue Datei: " + job.TempPath;
        }
    }

    private void RefreshLibrary(Job job)
    {
        try
        {
            _index.Invalidate();
            _libraryMonitor.ReportFileSystemChanged(job.TargetPath);
            if (!string.Equals(job.TargetPath, job.SourcePath, StringComparison.Ordinal))
            {
                _libraryMonitor.ReportFileSystemChanged(job.SourcePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codec Converter: Bibliothek konnte nicht aktualisiert werden");
        }
    }

    /// <summary>
    /// Bringt nach einem Neustart oder Absturz unterbrochene Jobs in einen sauberen Zustand.
    /// </summary>
    private void Recover()
    {
        foreach (var snapshot in _store.Snapshot())
        {
            var job = _store.Find(snapshot.Id);
            if (job is null || !JobStore.IsOpen(job) || job.State == JobState.Queued)
            {
                continue;
            }

            if (!job.TempPath.EndsWith(TempExtension, StringComparison.Ordinal))
            {
                Fail(job, "Unerwarteter Eintrag in jobs.json. Bitte von Hand prüfen.");
                continue;
            }

            KillOrphans(job);

            try
            {
                if (job.State is JobState.Running or JobState.Paused)
                {
                    DeleteIfExists(job.TempPath);
                    _store.Update(job, j =>
                    {
                        j.State = JobState.Queued;
                        j.Progress = 0;
                    });
                }
                else if (job.State == JobState.Finalizing)
                {
                    RecoverFinalizing(job);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(job, "Wiederaufnahme nach Neustart fehlgeschlagen: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Beendet ein ffmpeg, das einen Absturz des Servers ueberlebt hat (etwa weil es gerade angehalten war).
    /// Erkannt wird es an der einmaligen temporaeren Datei in seiner Befehlszeile. Nur unter Linux.
    /// </summary>
    private void KillOrphans(Job job)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return;
        }

        var needle = "file:" + job.TempPath;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                var arguments = File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0');
                if (arguments.Contains(needle, StringComparer.Ordinal))
                {
                    using var orphan = Process.GetProcessById(pid);
                    orphan.Kill(true);
                    orphan.WaitForExit(5000);
                    _logger.LogInformation("Codec Converter: verwaistes ffmpeg (PID {Pid}) beendet", pid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Prozess inzwischen beendet oder gehoert einem anderen Benutzer.
            }
        }
    }

    private void RecoverFinalizing(Job job)
    {
        var tempExists = File.Exists(job.TempPath);
        var sourceExists = File.Exists(job.SourcePath);
        var backupExists = job.BackupFile is not null && File.Exists(job.BackupFile);

        if (tempExists && backupExists && !sourceExists)
        {
            // Das Original war schon gesichert, nur das Umbenennen der neuen Datei fehlte noch.
            File.Move(job.TempPath, job.TargetPath, false);
            var size = new FileInfo(job.TargetPath).Length;
            _store.Update(job, j =>
            {
                j.State = JobState.Done;
                j.ResultSize = size;
                j.Finished = DateTime.UtcNow;
                j.Message = "Original gesichert unter " + j.BackupFile;
            });
            return;
        }

        if (tempExists)
        {
            // Solange die temporaere Datei existiert, ist die Datei am Quellpfad das Original.
            // Eine angefangene Kopie im Sicherungsordner ist unvollstaendig und wird verworfen.
            if (sourceExists && backupExists)
            {
                File.Delete(job.BackupFile!);
            }

            File.Delete(job.TempPath);
            _store.Update(job, j =>
            {
                j.State = sourceExists ? JobState.Queued : JobState.Failed;
                j.Progress = 0;
                j.BackupFile = null;
                j.Message = sourceExists ? null : "Nach einem Absturz fehlt die Quelldatei. Bitte von Hand prüfen.";
            });
            return;
        }

        var done = File.Exists(job.TargetPath) && backupExists;
        _store.Update(job, j =>
        {
            j.State = done ? JobState.Done : JobState.Failed;
            j.Finished = DateTime.UtcNow;
            j.Message = done ? "Original gesichert unter " + j.BackupFile : "Nach einem Absturz ist der Stand unklar. Bitte von Hand prüfen.";
        });
    }

    /// <summary>
    /// Loescht gesicherte Originale nach Ablauf der Aufbewahrungszeit.
    /// </summary>
    private void CleanupBackups()
    {
        if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastCleanup = DateTime.UtcNow;
        var days = Config.BackupRetentionDays;
        var root = GetBackupRoot(out _);
        if (days <= 0 || root is null || !Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            try
            {
                // Nur Ordner anfassen, die dieses Plugin selbst angelegt hat, und keinen Links folgen.
                var marker = new FileInfo(Path.Combine(directory.FullName, BackupMarker));
                if (directory.LinkTarget is not null || !marker.Exists || marker.LinkTarget is not null)
                {
                    continue;
                }

                if (DateTime.UtcNow - marker.LastWriteTimeUtc > TimeSpan.FromDays(days))
                {
                    directory.Delete(true);
                    _logger.LogInformation("Codec Converter: Sicherung {Directory} nach {Days} Tagen gelöscht", directory.Name, days);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Codec Converter: Sicherung {Directory} konnte nicht gelöscht werden", directory.Name);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        public static extern int Kill(int pid, int signal);
    }
}
