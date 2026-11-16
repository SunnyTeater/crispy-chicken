using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Haelt die Warteschlange im Speicher und schreibt sie nach jeder Aenderung auf die Platte.
/// </summary>
public sealed class JobStore
{
    /// <summary>
    /// Hoechstzahl an Jobs in der Liste.
    /// </summary>
    public const int MaxJobs = 5000;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly ILogger<JobStore> _logger;
    private readonly string _file;
    private List<Job> _jobs = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JobStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Pfade des Servers.</param>
    /// <param name="logger">Der Logger.</param>
    public JobStore(IApplicationPaths applicationPaths, ILogger<JobStore> logger)
    {
        _logger = logger;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "codec-converter");
        _file = Path.Combine(DataDirectory, "jobs.json");
        Load();
    }

    /// <summary>
    /// Gets den Datenordner des Plugins.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Liefert Kopien aller Jobs.
    /// </summary>
    /// <returns>Die Jobs, neueste zuerst.</returns>
    public List<Job> Snapshot()
    {
        lock (_lock)
        {
            return _jobs.Select(j => j.Copy()).OrderByDescending(j => j.Created).ToList();
        }
    }

    /// <summary>
    /// Fuegt Jobs hinzu.
    /// </summary>
    /// <param name="jobs">Die neuen Jobs.</param>
    public void AddRange(IEnumerable<Job> jobs)
    {
        lock (_lock)
        {
            _jobs.AddRange(jobs);
            Save();
        }
    }

    /// <summary>
    /// Gets die Anzahl aller Jobs.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Count;
            }
        }
    }

    /// <summary>
    /// Liefert den aeltesten wartenden Job (das Original, keine Kopie).
    /// </summary>
    /// <returns>Der Job oder null.</returns>
    public Job? NextQueued()
    {
        lock (_lock)
        {
            return _jobs.Where(j => j.State == JobState.Queued).OrderBy(j => j.Created).FirstOrDefault();
        }
    }

    /// <summary>
    /// Sucht einen Job.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <returns>Der Job oder null.</returns>
    public Job? Find(string id)
    {
        lock (_lock)
        {
            return _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Aendert einen Job unter dem Lock und speichert.
    /// </summary>
    /// <param name="job">Der Job.</param>
    /// <param name="change">Die Aenderung.</param>
    /// <param name="persist">Ob gespeichert werden soll (bei reinem Fortschritt nicht noetig).</param>
    public void Update(Job job, Action<Job> change, bool persist = true)
    {
        lock (_lock)
        {
            change(job);
            if (persist)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Entfernt Jobs.
    /// </summary>
    /// <param name="predicate">Welche Jobs entfernt werden.</param>
    /// <returns>Anzahl der entfernten Jobs.</returns>
    public int Remove(Func<Job, bool> predicate)
    {
        lock (_lock)
        {
            var removed = _jobs.RemoveAll(j => predicate(j));
            if (removed > 0)
            {
                Save();
            }

            return removed;
        }
    }

    /// <summary>
    /// Ob ein Job noch nicht abgeschlossen ist.
    /// </summary>
    /// <param name="job">Der Job.</param>
    /// <returns>True, wenn offen.</returns>
    public static bool IsOpen(Job job)
        => job.State is JobState.Queued or JobState.Running or JobState.Paused or JobState.Finalizing or JobState.AwaitingApproval;

    /// <summary>
    /// Liefert zu jedem Element mit offenem Job dessen Zustand.
    /// </summary>
    /// <returns>Item-Id und Zustand.</returns>
    public Dictionary<Guid, JobState> OpenItemStates()
    {
        lock (_lock)
        {
            var result = new Dictionary<Guid, JobState>();
            foreach (var job in _jobs.Where(IsOpen))
            {
                result[job.ItemId] = job.State;
            }

            return result;
        }
    }

    /// <summary>
    /// Liefert die Ids der Jobs in einem Zustand.
    /// </summary>
    /// <param name="state">Der Zustand.</param>
    /// <returns>Die Job-Ids, aelteste zuerst.</returns>
    public List<string> IdsInState(JobState state)
    {
        lock (_lock)
        {
            return _jobs.Where(j => j.State == state).OrderBy(j => j.Created).Select(j => j.Id).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_file), _json) ?? new List<Job>();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Codec Converter: jobs.json konnte nicht gelesen werden, die Warteschlange startet leer");
            _jobs = new List<Job>();
            try
            {
                // Die unlesbare Datei aufheben, statt sie beim naechsten Speichern zu ueberschreiben.
                File.Copy(_file, _file + ".defekt", true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(copyEx, "Codec Converter: defekte jobs.json konnte nicht gesichert werden");
            }
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);

            // Erst in eine Nachbardatei schreiben und dann umbenennen: ein Absturz hinterlaesst nie eine halbe jobs.json.
            var temp = _file + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_jobs, _json));
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Codec Converter: jobs.json konnte nicht gespeichert werden");
        }
    }
}
