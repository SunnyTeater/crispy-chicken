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
public sealed partial class JobRunner : IHostedService, IDisposable
{
    /// <summary>
    /// Hinweis fuer Dateien, die schon umgewandelt sind, in Jellyfins Bibliothek aber noch mit den alten Daten stehen.
    /// </summary>
    public const string AwaitingApprovalText = "Umgewandelt, wartet auf Freigabe";

    private const string TempExtension = ".ccpart";

    private static readonly HashSet<string> _sidecarExtensions = new(StringComparer.Ordinal)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi"
    };

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(60);

    // So viel bleibt auf dem Datentraeger der Medien immer frei. Ein randvoller Datentraeger legt mehr lahm als
    // nur die Umwandlung (Downloads, andere Dienste, bei gleichem Datentraeger auch Jellyfins Datenbank).
    private const long MinFreeBytes = 256L * 1024 * 1024;
    private const string BackupMarker = ".codec-converter-backup";

    private readonly JobStore _store;
    private readonly LibraryIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<JobRunner> _logger;
    private readonly LiveLog _log;
    private readonly Stopwatch _activeWatch = new();
    private readonly Dictionary<string, string> _progressBlock = new(StringComparer.Ordinal);
    private DateTime _lastProgressLog = DateTime.MinValue;
    private bool? _lastBlocked;
    private readonly SourceInfoCache _sourceInfo;
    private double? _currentExpectedSpeed;
    private DateTime _lastDeletedCheck = DateTime.MinValue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private readonly object _planLock = new();
    private readonly SemaphoreSlim _approvalLock = new(1, 1);
    private DateTime _lastAutoApprove = DateTime.MinValue;
    private DateTime _lastUserStateRun = DateTime.MinValue;
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
    /// <param name="userManager">Die Benutzerverwaltung.</param>
    /// <param name="userDataManager">Die Benutzerdaten (Gesehen-Status, Fortschritt).</param>
    /// <param name="mediaEncoder">Jellyfins ffmpeg-Zugang.</param>
    /// <param name="logger">Der Logger.</param>
    /// <param name="log">Das Live-Protokoll.</param>
    public JobRunner(
        JobStore store,
        LibraryIndex index,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ISessionManager sessionManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IMediaEncoder mediaEncoder,
        ILogger<JobRunner> logger,
        LiveLog log,
        SourceInfoCache sourceInfo)
    {
        _log = log;
        _sourceInfo = sourceInfo;
        _store = store;
        _index = index;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
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
    /// Gets die Frist der automatischen Freigabe in Tagen (0 = nie).
    /// </summary>
    public int AutoApproveDays => Math.Clamp(Config.AutoApproveDays, 0, 3650);

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
    /// Schaetzt die restliche Rechenzeit eines Jobs in Sekunden. Grundlage sind die Zeitangaben von ffmpeg:
    /// beim laufenden Job der bisher umgewandelte Anteil und die dafuer gebrauchte Rechenzeit, bei wartenden Jobs
    /// die Geschwindigkeit frueherer Jobs mit demselben Encoder, umgerechnet auf Aufloesung und Bildrate.
    /// </summary>
    /// <param name="job">Der Job (Kopie oder Original).</param>
    /// <param name="all">Alle Jobs.</param>
    /// <returns>Sekunden oder null, wenn noch keine Grundlage da ist.</returns>
    public static double? EstimateSeconds(Job job, IReadOnlyDictionary<(string Encoder, string Speed), double> speeds)
    {
        if (job.DurationSeconds <= 0)
        {
            return null;
        }

        var expected = ExpectedSpeed(speeds, job.Encoder, job.Speed, job.Width, job.Height, job.Fps);
        if (job.State is JobState.Running or JobState.Paused)
        {
            // Der Encoder liefert die ersten Bilder mit Verzoegerung: ganz am Anfang wirkt der Job langsamer, als er ist.
            // Solange weniger als 5 % umgewandelt sind, zaehlt deshalb der Erfahrungswert frueherer Jobs, falls es einen gibt.
            if (job.Progress < 0.05 && expected is not null)
            {
                return job.DurationSeconds * (1 - job.Progress) / expected.Value;
            }

            if (job.ActiveSeconds < 10 || job.Progress < 0.005)
            {
                return expected is null ? null : job.DurationSeconds * (1 - job.Progress) / expected.Value;
            }

            var speed = job.Progress * job.DurationSeconds / job.ActiveSeconds;
            return speed <= 0 ? null : job.DurationSeconds * (1 - job.Progress) / speed;
        }

        return expected is null ? null : job.DurationSeconds / expected.Value;
    }

    /// <summary>
    /// Bildet aus der Job-Liste je Encoder und Geschwindigkeitsstufe den Erfahrungswert "Bildpunkte pro Sekunde
    /// Rechenzeit" (Median der letzten fuenf Jobs). Ein Durchlauf ueber alle Jobs, damit die Abfragen der Seite
    /// auch bei Tausenden Jobs schnell bleiben.
    /// </summary>
    /// <param name="all">Alle Jobs.</param>
    /// <returns>Die Tabelle.</returns>
    public static Dictionary<(string Encoder, string Speed), double> BuildSpeedTable(IReadOnlyList<Job> all)
    {
        var samples = new Dictionary<(string, string), List<(DateTime When, double Throughput)>>();
        foreach (var job in all)
        {
            if (job.Width <= 0 || job.Height <= 0)
            {
                continue;
            }

            double speed = 0;
            if (job.AverageSpeed > 0)
            {
                speed = job.AverageSpeed;
            }
            else if (job.State is JobState.Running or JobState.Paused && job.ActiveSeconds >= 30 && job.Progress >= 0.02)
            {
                speed = job.Progress * job.DurationSeconds / job.ActiveSeconds;
            }

            if (speed <= 0)
            {
                continue;
            }

            var key = (job.Encoder, job.Speed);
            if (!samples.TryGetValue(key, out var list))
            {
                samples[key] = list = new List<(DateTime, double)>();
            }

            list.Add((job.Started ?? job.Created, speed * Load(job.Width, job.Height, job.Fps)));
        }

        var table = new Dictionary<(string, string), double>();
        foreach (var (key, list) in samples)
        {
            var recent = list.OrderByDescending(s => s.When).Take(5).Select(s => s.Throughput).OrderBy(t => t).ToList();
            table[key] = recent[recent.Count / 2];
        }

        return table;
    }

    /// <summary>
    /// Gleicht das Rechenmodell mit den eigenen Ergebnissen ab: je Encoder das mittlere Verhaeltnis aus
    /// tatsaechlicher und vom Modell geschaetzter Groesse der letzten Umwandlungen (Median, ab drei Ergebnissen).
    /// </summary>
    /// <param name="all">Alle Jobs.</param>
    /// <returns>Korrekturfaktor je Encoder.</returns>
    public static Dictionary<string, double> BuildSizeCorrections(IReadOnlyList<Job> all)
    {
        var table = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in all.Where(j => j.ModelSize > 0 && j.ResultSize > 0 && j.SourceErrors == 0).GroupBy(j => j.Encoder, StringComparer.Ordinal))
        {
            var ratios = group.OrderByDescending(j => j.Converted ?? j.Finished ?? j.Created).Take(15)
                .Select(j => (double)j.ResultSize! / j.ModelSize).OrderBy(r => r).ToList();
            if (ratios.Count >= 3)
            {
                table[group.Key] = Math.Clamp(ratios[ratios.Count / 2], 0.5, 2.0);
            }
        }

        return table;
    }

    /// <summary>
    /// Korrekturfaktor fuer einen Encoder, 1 ohne eigene Ergebnisse.
    /// </summary>
    /// <param name="corrections">Die Tabelle aus <see cref="BuildSizeCorrections"/>.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <returns>Der Faktor.</returns>
    public static double SizeCorrection(IReadOnlyDictionary<string, double> corrections, string encoder)
        => corrections.TryGetValue(encoder, out var factor) ? factor : 1;

    /// <summary>
    /// Erwartete Geschwindigkeit (Vielfaches der Echtzeit) fuer einen Encoder bei gegebener Aufloesung.
    /// </summary>
    /// <param name="speeds">Die Tabelle aus <see cref="BuildSpeedTable"/>.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <param name="speedSetting">Die Geschwindigkeitsstufe.</param>
    /// <param name="width">Breite.</param>
    /// <param name="height">Hoehe.</param>
    /// <param name="fps">Bildrate.</param>
    /// <returns>Geschwindigkeit oder null ohne Erfahrungswerte.</returns>
    public static double? ExpectedSpeed(IReadOnlyDictionary<(string Encoder, string Speed), double> speeds, string encoder, string speedSetting, int width, int height, double fps)
    {
        if (width <= 0 || height <= 0 || !speeds.TryGetValue((encoder, speedSetting), out var throughput))
        {
            return null;
        }

        return throughput / Load(width, height, fps);
    }

    private static double Load(int width, int height, double fps)
        => Math.Max(1, width) * (double)Math.Max(1, height) * (fps is > 1 and < 300 ? fps : 24);

    /// <summary>
    /// Ob die Datei eines Jobs gerade in einer Sitzung abgespielt wird.
    /// </summary>
    private bool IsBeingPlayed(Job job)
    {
        var directory = Path.GetDirectoryName(job.SourcePath);
        bool? wholeFolder = null;
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is null)
            {
                continue;
            }

            if (session.NowPlayingItem.Id.Equals(job.ItemId))
            {
                return true;
            }

            var path = session.FullNowPlayingItem?.Path;
            if (path is null)
            {
                continue;
            }

            if (string.Equals(path, job.SourcePath, StringComparison.Ordinal)
                || string.Equals(path, job.TargetPath, StringComparison.Ordinal)
                || string.Equals(path, job.OriginalPath, StringComparison.Ordinal))
            {
                return true;
            }

            // Bei Versionen spielt Jellyfin ueber die Hauptversion ab, die auch die andere Datei sein kann.
            // Ein Filmordner gehoert zu genau einem Film: laeuft dort irgendetwas, gilt der Film als belegt.
            wholeFolder ??= directory is not null && IsMovie(job) && !IsLibraryRoot(directory);
            if (wholeFolder.Value && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal))
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
        var open = _store.OpenItemStates();
        var room = JobStore.MaxJobs - _store.Count;
        var corrections = BuildSizeCorrections(_store.Snapshot());

        foreach (var id in request.ItemIds.Distinct())
        {
            var entry = _index.GetEntry(id, out var path);
            if (entry is null || string.IsNullOrEmpty(path))
            {
                skipped.Add("Unbekanntes Element " + id.ToString("N", CultureInfo.InvariantCulture));
                continue;
            }

            string? reason = entry.Blocked;
            if (reason is null && open.TryGetValue(id, out var openState))
            {
                reason = openState == JobState.AwaitingApproval ? AwaitingApprovalText : "bereits in der Warteschlange";
            }

            // Die neue Datei kommt neben das Original und heisst nach Jellyfins Versionsregel "Ordnername-HEVC".
            // Der endgueltige Name wird beim Fertigstellen noch einmal geprueft.
            var directory = Path.GetDirectoryName(path)!;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var targetPath = string.Empty;
            if (reason is null)
            {
                try
                {
                    var reserved = ReservedTargets(string.Empty);
                    reserved.UnionWith(jobs.Select(j => j.TargetPath));
                    var movie = entry.Type == "Movie";
                    targetPath = PlanNames(path, entry.Codec, target.Codec, movie, movie ? null : EpisodeInfo(id, path), null, reserved).TargetPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reason = "Ordner nicht lesbar: " + ex.Message;
                }
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
            _sourceInfo.TryGet(entry, out var info);
            var modelSize = EncoderCatalog.EstimateSize(entry, target, encoder, request.Quality, info);
            jobs.Add(new Job
            {
                Id = jobId,
                ItemId = id,
                Name = DisplayName(entry),
                SourcePath = path,

                // Kein Video-Dateiname: Jellyfin ignoriert die halbfertige Datei beim Scannen.
                TempPath = Path.Combine(directory, baseName + "." + jobId[..16] + TempExtension),
                TargetPath = targetPath,
                ItemType = entry.Type,
                SourceCodec = entry.Codec,
                TargetCodec = target.Codec,
                Encoder = encoder.Name,
                Quality = request.Quality,
                Speed = request.Speed,
                SourceSize = entry.Size,
                ModelSize = modelSize,
                EstimatedSize = (long)(modelSize * SizeCorrection(corrections, encoder.Name)),
                SourceCrf = info?.Crf,
                DurationSeconds = entry.DurationSeconds,
                Width = entry.Width,
                Height = entry.Height,
                Fps = entry.Fps,
                State = JobState.Queued,
                Created = DateTime.UtcNow.AddTicks(jobs.Count)
            });
            open[id] = JobState.Queued;
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
                case JobState.AwaitingApproval:
                    if (FilesDeleted(job) is not null)
                    {
                        _store.Remove(j => string.Equals(j.Id, id, StringComparison.Ordinal));
                        return null;
                    }

                    return "Dieser Job wartet auf Freigabe: bitte freigeben oder verwerfen.";
                default:
                    if (job.PendingUserStates.Count > 0)
                    {
                        return "Der Gesehen-Status wird noch auf die neue Datei übertragen. Bitte in ein bis zwei Minuten erneut entfernen.";
                    }

                    _store.Remove(j => string.Equals(j.Id, id, StringComparison.Ordinal));
                    return null;
            }
        }
    }

    /// <summary>
    /// Prueft, ob die Dateien eines Jobs ausserhalb des Plugins geloescht wurden (im Dateisystem oder ueber Jellyfin).
    /// Liefert den Grund oder null. Ist die Bibliothek gerade nicht erreichbar (Netzlaufwerk nicht eingehaengt),
    /// gilt nichts als geloescht: sonst ginge bei einem Ausfall die ganze Warteschlange verloren.
    /// </summary>
    private string? FilesDeleted(Job job)
    {
        if (job.PendingRename is not null)
        {
            return null;
        }

        try
        {
            var sourceGone = !File.Exists(job.SourcePath);
            var targetGone = job.State == JobState.AwaitingApproval && !File.Exists(job.TargetPath);
            if ((!sourceGone && !targetGone) || !LibraryReachable(job.SourcePath))
            {
                return null;
            }

            if (job.State == JobState.Queued)
            {
                return "die Datei wurde gelöscht";
            }

            return sourceGone && targetGone ? "Original und neue Datei wurden gelöscht"
                : sourceGone ? "das Original wurde gelöscht, die neue Datei bleibt"
                : "die neue Datei wurde gelöscht, das Original bleibt";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool LibraryReachable(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var location in _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations ?? Array.Empty<string>()))
        {
            var library = WithSeparator(Path.GetFullPath(location));
            if (full.StartsWith(library, StringComparison.Ordinal))
            {
                return Directory.Exists(library) && Directory.EnumerateFileSystemEntries(library).Any();
            }
        }

        return false;
    }

    /// <summary>
    /// Nimmt Jobs aus der Warteschlange, deren Dateien inzwischen geloescht wurden: wartende Jobs ohne Quelle und
    /// Jobs vor der Freigabe, bei denen Original oder neue Datei fehlt (dann gibt es nichts mehr zu entscheiden).
    /// </summary>
    private void RemoveJobsOfDeletedFiles()
    {
        if (DateTime.UtcNow - _lastDeletedCheck < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastDeletedCheck = DateTime.UtcNow;
        try
        {
            RemoveJobsOfDeletedFilesCore();
        }
        catch (Exception ex)
        {
            // Aufraeumen ist Nebensache: ein Fehler hier darf keine laufende Umwandlung stoeren.
            _logger.LogWarning(ex, "Codec Converter: Pruefung auf geloeschte Dateien fehlgeschlagen");
        }
    }

    private void RemoveJobsOfDeletedFilesCore()
    {
        foreach (var snapshot in _store.Snapshot().Where(j => j.State is JobState.Queued or JobState.AwaitingApproval))
        {
            lock (_lock)
            {
                var job = _store.Find(snapshot.Id);
                if (job is null || job.State != snapshot.State || string.Equals(_currentJobId, job.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var reason = FilesDeleted(job);
                if (reason is null)
                {
                    continue;
                }

                _store.Remove(j => string.Equals(j.Id, job.Id, StringComparison.Ordinal));
                _log.Add("Aus der Warteschlange entfernt: " + job.Name + " (" + reason + ").");
                _logger.LogInformation("Codec Converter: Job {Name} entfernt, {Reason}", job.Name, reason);
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
        _approvalLock.Dispose();
    }

    private static string DisplayName(MediaEntry entry)
    {
        // Steuerzeichen (etwa Zeilenumbrueche in Dateinamen) koennten in Protokollen zusaetzliche Zeilen vortaeuschen.
        return new string(RawDisplayName(entry).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }

    private static string RawDisplayName(MediaEntry entry)
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
                RemoveJobsOfDeletedFiles();
                await FixNamingAsync(token).ConfigureAwait(false);
                await AutoApproveAsync(token).ConfigureAwait(false);
                RestoreUserStates();

                var job = _store.NextQueued();
                var blockedNow = IsBlocked;
                if (job is not null && _lastBlocked != blockedNow)
                {
                    _lastBlocked = blockedNow;
                    _log.Add(blockedNow
                        ? "Warteschlange wartet: " + ActiveStreams + " Streams aktiv (Grenze " + StreamLimit + ")."
                        : "Warteschlange läuft: " + ActiveStreams + " Streams aktiv (Grenze " + StreamLimit + ").");
                }

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

    /// <summary>
    /// Uebersetzt haeufige ffmpeg-Fehler in einen Satz, der die Ursache nennt.
    /// </summary>
    private static string ExplainFailure(string errorText)
    {
        // Reihenfolge wichtig: ein Encoder, der nicht startet, zieht dieselben Folgemeldungen nach sich wie eine unlesbare Quelle.
        if (errorText.Contains("Cannot open libx265 encoder", StringComparison.Ordinal)
            || errorText.Contains("Error while opening encoder", StringComparison.Ordinal)
            || errorText.Contains("Error initializing output stream", StringComparison.Ordinal))
        {
            return "Der Encoder ließ sich mit diesen Einstellungen nicht starten; die erste ffmpeg-Zeile nennt den Grund (bei Hardware-Encodern meist fehlende Grafikkarte oder Treiber). Das Original wurde nicht angetastet. ";
        }

        if (errorText.Contains("Cannot determine format of input", StringComparison.Ordinal))
        {
            return "Die Videospur der Quelle lieferte kein einziges lesbares Bild: die Datei ist beschädigt oder unvollständig, oder Jellyfins ffmpeg kann ihren Codec nicht dekodieren. Das Original wurde nicht angetastet. ";
        }

        if (errorText.Contains("No space left on device", StringComparison.Ordinal))
        {
            return "Der Datenträger ist voll. Das Original wurde nicht angetastet. ";
        }

        if (errorText.Contains("Permission denied", StringComparison.Ordinal))
        {
            return "Dem Jellyfin-Dienst fehlen Schreibrechte im Ordner der Datei. Das Original wurde nicht angetastet. ";
        }

        return string.Empty;
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
    }

    private void Fail(Job job, string message)
    {
        _logger.LogWarning("Codec Converter: {Name} fehlgeschlagen: {Message}", job.Name, message);
        _log.Add("FEHLER " + job.Name + ": " + message);
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

        var source = new FileInfo(job.SourcePath);
        if (!source.Exists)
        {
            return "Die Quelldatei existiert nicht mehr.";
        }

        if (source.LinkTarget is not null)
        {
            return "Die Quelldatei ist ein Symlink. Symlinks werden nicht umgewandelt.";
        }

        if (File.Exists(job.TargetPath))
        {
            return "Die Zieldatei " + Path.GetFileName(job.TargetPath) + " existiert bereits.";
        }

        try
        {
            // Original und neue Datei liegen bis zur Freigabe nebeneinander. Die Schaetzung kann deutlich zu
            // klein sein, deshalb mit Aufschlag rechnen und die Reserve dazunehmen, die auch waehrend des Laufs gilt.
            var needed = (long)(Math.Max(job.EstimatedSize, 0) * 1.5) + MinFreeBytes;
            if (new DriveInfo(source.DirectoryName!).AvailableFreeSpace < needed)
            {
                return "Zu wenig freier Speicher im Medienordner: nötig sind die geschätzte neue Datei mit Aufschlag und "
                    + (MinFreeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB Reserve.";
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
            var gpuDecode = EncoderCatalog.UsesGpuDecode(job.Encoder);
            List<string> arguments;
            try
            {
                arguments = EncoderCatalog.BuildArguments(job, format, Math.Clamp(Config.Threads, 0, 256), Config.VaapiDevice ?? string.Empty, gpuDecode);
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
            _log.Add("START " + job.Name + ": " + job.SourceCodec + " → " + job.TargetCodec + " mit " + job.Encoder + ", Qualitätswert " + job.Quality + ", Geschwindigkeit " + job.Speed + ", Laufzeit " + Clock(job.DurationSeconds));
            _log.Add("Quelle: " + Path.GetFileName(job.SourcePath) + "  →  Ziel: " + Path.GetFileName(job.TargetPath));
            _log.Add("ffmpeg " + string.Join(' ', arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)));

            var (exitCode, errorText, cancelled) = await RunFfmpegAsync(job, arguments, token).ConfigureAwait(false);

            if (gpuDecode && exitCode != 0 && !cancelled && !token.IsCancellationRequested)
            {
                // Nicht jede Grafikkarte kann jede Quelle dekodieren (alte Codecs, 10 Bit ...): zweiter Versuch,
                // bei dem die CPU dekodiert und nur das Kodieren auf der GPU laeuft.
                _logger.LogInformation("Codec Converter: {Name}: Dekodieren auf der GPU fehlgeschlagen ({Error}), zweiter Versuch mit CPU-Dekodierung", job.Name, errorText);
                _log.Add("Dekodieren auf der Grafikkarte fehlgeschlagen, zweiter Versuch mit CPU-Dekodierung.");
                DeleteIfExists(job.TempPath);
                _store.Update(job, j => j.Progress = 0);
                arguments = EncoderCatalog.BuildArguments(job, format, Math.Clamp(Config.Threads, 0, 256), Config.VaapiDevice ?? string.Empty, false);
                (exitCode, errorText, cancelled) = await RunFfmpegAsync(job, arguments, token).ConfigureAwait(false);
            }

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
                _log.Add("ABGEBROCHEN " + job.Name + ", halbfertige Datei gelöscht.");
                return;
            }

            if (exitCode == 0 && job.ActiveSeconds > 0)
            {
                // Erfahrungswert fuer die Zeitschaetzung der naechsten Jobs.
                _store.Update(job, j => j.AverageSpeed = j.DurationSeconds / j.ActiveSeconds);
                _log.Add("ffmpeg fertig nach " + Clock(job.ActiveSeconds) + " Rechenzeit (" + job.AverageSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "× Echtzeit). Prüfe die neue Datei …");
            }

            if (exitCode != 0)
            {
                DeleteIfExists(job.TempPath);
                Fail(job, exitCode == -1 ? errorText : ExplainFailure(errorText) + "ffmpeg ist mit Code " + exitCode + " gescheitert: " + (errorText.Length == 0 ? "keine Fehlermeldung" : errorText));
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

        // SVT-AV1 schreibt sonst bei jedem Lauf Infozeilen in die Fehlerausgabe (1 = nur Fehler).
        startInfo.Environment["SVT_LOG"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var errors = new StringBuilder();
        var head = new StringBuilder();
        var errorLines = 0;
        var stalled = false;
        var diskFull = false;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // Reine Info- und Warnzeilen der Encoder sind keine Fehler.
            if (e.Data.Length == 0
                || e.Data.StartsWith("Svt[info]", StringComparison.Ordinal)
                || e.Data.StartsWith("Svt[warn]", StringComparison.Ordinal)
                || e.Data.StartsWith("x265 [info]", StringComparison.Ordinal)
                || e.Data.StartsWith("x265 [warning]", StringComparison.Ordinal))
            {
                return;
            }

            lock (errors)
            {
                errorLines++;

                // Eine beschaedigte Datei erzeugt Tausende gleichartiger Meldungen: nur die ersten ins Live-Protokoll.
                if (errorLines <= 15)
                {
                    _log.Add("ffmpeg: " + e.Data);
                }
                else if (errorLines == 16)
                {
                    _log.Add("ffmpeg: … weitere Meldungen werden nicht mehr einzeln angezeigt.");
                }

                // Die Ursache steht meist am Anfang, das Ergebnis am Ende: beides aufheben.
                if (head.Length < 700)
                {
                    head.AppendLine(e.Data);
                }

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
        _lastProgressLog = DateTime.MinValue;
        _progressBlock.Clear();
        _currentExpectedSpeed = ExpectedSpeed(BuildSpeedTable(_store.Snapshot()), job.Encoder, job.Speed, job.Width, job.Height, job.Fps);
        _activeWatch.Restart();
        _store.Update(job, j => j.ActiveSeconds = 0, persist: false);
        while (!process.HasExited)
        {
            // Auch waehrend einer langen Umwandlung aufraeumen, nicht erst danach.
            RemoveJobsOfDeletedFiles();

            if (!paused && FreeSpaceLow(job))
            {
                // Die neue Datei wird groesser als geschaetzt und der Datentraeger laeuft voll: abbrechen, bevor er voll ist.
                _log.Add("Freier Speicher im Medienordner unter " + (MinFreeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, breche ab.");
                KillProcess();
                diskFull = true;
                break;
            }

            if (!paused && DateTime.UtcNow - _lastOutput > StallTimeout)
            {
                // Ein haengendes ffmpeg wuerde sonst die ganze Warteschlange blockieren.
                _logger.LogWarning("Codec Converter: ffmpeg meldet seit {Minutes} Minuten nichts mehr und wird beendet", StallTimeout.TotalMinutes);
                _log.Add("ffmpeg meldet seit " + StallTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " Minuten nichts mehr und wird beendet.");
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
                _activeWatch.Stop();
                _log.Add("PAUSE " + job.Name + ": " + ActiveStreams + " Streams aktiv (Grenze " + StreamLimit + ").");
                _store.Update(job, j => j.State = JobState.Paused);
                _logger.LogInformation("Codec Converter: {Name} angehalten, {Streams} Streams aktiv", job.Name, ActiveStreams);
            }
            else if (!blocked && paused && Signal(process, stop: false))
            {
                paused = false;
                _activeWatch.Start();
                _log.Add("WEITER " + job.Name + ": Streams wieder unter der Grenze.");
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
        _activeWatch.Stop();
        _store.Update(job, j => j.ActiveSeconds = _activeWatch.Elapsed.TotalSeconds, persist: false);

        string errorText;
        int sourceErrors;
        lock (errors)
        {
            sourceErrors = errorLines;
            var all = errors.ToString().Trim();
            var start = head.ToString().Trim();
            if (start.Length > 500)
            {
                start = start[..500];
            }

            // Kurze Ausgaben ganz, lange als Anfang und Ende.
            errorText = all.Length <= 1000 ? all : start + " … " + all[^450..];
        }

        _store.Update(job, j => j.SourceErrors = sourceErrors, persist: false);

        if (diskFull)
        {
            return (-1, "Der Datenträger wurde zu voll: die neue Datei wird größer als geschätzt. Abgebrochen, damit mindestens "
                + (MinFreeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB frei bleiben. Das Original wurde nicht angetastet.", false);
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

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator];
        var value = line[(separator + 1)..].Trim();
        _progressBlock[key] = value;

        if (key == "out_time_us"
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            && microseconds > 0)
        {
            var progress = Math.Clamp(microseconds / 1_000_000d / job.DurationSeconds, 0, 0.999);
            var active = _activeWatch.Elapsed.TotalSeconds;
            _store.Update(
                job,
                j =>
                {
                    j.Progress = progress;
                    j.ActiveSeconds = active;
                },
                persist: false);
        }
        else if (key == "speed"
            && double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            _store.Update(job, j => j.EncodeSpeed = speed, persist: false);
        }
        else if (key == "progress" && DateTime.UtcNow - _lastProgressLog > TimeSpan.FromSeconds(5))
        {
            // "progress" schliesst einen Block von ffmpeg ab: alle paar Sekunden eine Zeile fuers Live-Protokoll.
            _lastProgressLog = DateTime.UtcNow;
            _progressBlock.TryGetValue("frame", out var frame);
            _progressBlock.TryGetValue("fps", out var fps);
            _progressBlock.TryGetValue("total_size", out var size);
            var megabytes = long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : "?";
            // Dieselbe Rechnung wie in der Warteschlange: am Anfang der Erfahrungswert frueherer Jobs, danach der eigene Verlauf.
            double? remaining = null;
            var ownHistory = job.ActiveSeconds >= 10 && job.Progress >= 0.005;
            if (_currentExpectedSpeed is not null && (job.Progress < 0.05 || !ownHistory))
            {
                remaining = job.DurationSeconds * (1 - job.Progress) / _currentExpectedSpeed.Value;
            }
            else if (ownHistory)
            {
                remaining = job.DurationSeconds * (1 - job.Progress) / (job.Progress * job.DurationSeconds / job.ActiveSeconds);
            }

            _log.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0:0} %  Bild {1}  {2} Bilder/s  Stelle {3} von {4}  bisher {5}  Rest ca. {6}",
                job.Progress * 100,
                frame ?? "?",
                fps ?? "?",
                Clock(job.Progress * job.DurationSeconds),
                Clock(job.DurationSeconds),
                megabytes,
                remaining is null ? "?" : Clock(remaining.Value)));
        }
    }

    private static bool FreeSpaceLow(Job job)
    {
        try
        {
            return new DriveInfo(Path.GetDirectoryName(job.TempPath)!).AvailableFreeSpace < MinFreeBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
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
    private async Task<string?> VerifyAsync(Job job, string path, CancellationToken token)
    {
        var temp = new FileInfo(path);
        if (!temp.Exists || temp.Length < 1024)
        {
            return "ffmpeg hat keine verwertbare Datei erzeugt.";
        }

        if (temp.LinkTarget is not null)
        {
            return "Die Datei ist ein Symlink.";
        }

        var result = await ProbeAsync(path, token).ConfigureAwait(false);
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

        var problem = await VerifyAsync(job, job.TempPath, token).ConfigureAwait(false);
        NamingPlan? plan = null;
        if (problem is null)
        {
            try
            {
                // Die Namen noch einmal pruefen: im Ordner kann sich seit dem Planen etwas geaendert haben.
                var movie = IsMovie(job);
                plan = PlanNames(job.SourcePath, job.SourceCodec, job.TargetCodec, movie, movie ? null : EpisodeInfo(job.ItemId, job.SourcePath), null, ReservedTargets(job.Id));
                if (File.Exists(plan.TargetPath))
                {
                    problem = "Die Zieldatei " + Path.GetFileName(plan.TargetPath) + " existiert inzwischen.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = "Der Ordner ließ sich nicht lesen: " + ex.Message;
            }
        }

        if (problem is not null || plan is null)
        {
            DeleteIfExists(job.TempPath);
            Fail(job, problem + " Das Original wurde nicht angetastet.");
            return;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Die neue Datei bekommt dieselben Zugriffsrechte wie das Original.
                File.SetUnixFileMode(job.TempPath, File.GetUnixFileMode(job.SourcePath));
            }

            // Erst den Namen speichern, dann verschieben: nach einem Absturz findet die Wiederaufnahme die Datei.
            _store.Update(job, j => j.TargetPath = plan.TargetPath);

            // Das Original bleibt, wo es ist. Die neue Datei erscheint unter ihrem endgueltigen Namen daneben.
            File.Move(job.TempPath, job.TargetPath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(job.TempPath);
            Fail(job, "Abschluss fehlgeschlagen: " + ex.Message + " Das Original wurde nicht angetastet.");
            return;
        }

        var sidecars = CopySidecars(job);
        var size = new FileInfo(job.TargetPath).Length;

        if (job.SourceErrors > 0)
        {
            _log.Add("ACHTUNG " + job.Name + ": ffmpeg meldete " + job.SourceErrors + " Fehler beim Lesen der Quelle. Keine automatische Freigabe.");
        }

        _logger.LogInformation("Codec Converter: {Name} umgewandelt, {Before} -> {After} Bytes, wartet auf Freigabe", job.Name, job.SourceSize, size);
        _log.Add(string.Format(
            CultureInfo.InvariantCulture,
            "FERTIG {0}: Prüfung bestanden, neue Datei {1} abgelegt ({2:0.0} MB statt {3:0.0} MB){4}. Wartet auf Freigabe.",
            job.Name,
            Path.GetFileName(job.TargetPath),
            size / 1048576d,
            job.SourceSize / 1048576d,
            sidecars.Count > 0 ? ", " + sidecars.Count + " Untertiteldatei(en) mitkopiert" : string.Empty));

        // Freigeben oder Verwerfen erst, wenn auch das Original seinen Namen hat.
        await _approvalLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _store.Update(job, j =>
            {
                j.State = JobState.AwaitingApproval;
                j.ResultSize = size;
                j.Finished = DateTime.UtcNow;
                j.Converted = DateTime.UtcNow;
                j.Sidecars = sidecars;
                j.NamingApplied = false;
                j.NamingVersion = 0;
                j.Message = AwaitingMessage(j);
            });

            if (plan.OriginalNewPath is not null && IsBeingPlayed(job))
            {
                // Eine Datei, die gerade laeuft, nicht umbenennen. Das holt die Warteschlange nach der Wiedergabe nach.
                _log.Add("Das Original von " + job.Name + " wird gerade abgespielt. Umbenennen nach Jellyfins Versionsregel folgt nach der Wiedergabe.");
            }
            else
            {
                try
                {
                    ApplyPlan(job, plan, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Codec Converter: Original von {Name} konnte nicht umbenannt werden, neuer Versuch später", job.Name);
                    _log.Add("Original von " + job.Name + " konnte nicht umbenannt werden (" + ex.Message + "), neuer Versuch später.");
                }
            }
        }
        finally
        {
            _approvalLock.Release();
        }


        RefreshLibrary(job);
    }

    /// <summary>
    /// Kopiert externe Untertitel des Originals unter dem Namen der neuen Datei, damit Jellyfin sie auch dort findet.
    /// </summary>
    private List<SidecarCopy> CopySidecars(Job job)
    {
        var result = new List<SidecarCopy>();
        try
        {
            var directory = Path.GetDirectoryName(job.SourcePath)!;
            var oldPrefix = Path.GetFileNameWithoutExtension(job.SourcePath) + ".";
            var newBase = Path.GetFileNameWithoutExtension(job.TargetPath);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith(oldPrefix, StringComparison.Ordinal)
                    || !_sidecarExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())
                    || new FileInfo(file).LinkTarget is not null)
                {
                    continue;
                }

                var copy = Path.Combine(directory, newBase + "." + name[oldPrefix.Length..]);
                if (!File.Exists(copy))
                {
                    File.Copy(file, copy, false);
                    result.Add(new SidecarCopy { Original = file, Copy = copy });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fehlende Untertitel sind kein Grund, die fertige Umwandlung zu verwerfen.
            _logger.LogWarning(ex, "Codec Converter: externe Untertitel von {Name} konnten nicht kopiert werden", job.Name);
        }

        return result;
    }

    /// <summary>
    /// Gibt eine umgewandelte Datei frei: das Original wird endgueltig geloescht.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <param name="automatic">Ob die Freigabe nach Fristablauf automatisch erfolgt.</param>
    /// <param name="token">Abbruch-Token.</param>
    /// <returns>Fehlertext oder null.</returns>
    public async Task<string?> ApproveAsync(string id, bool automatic, CancellationToken token)
    {
        await _approvalLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var job = _store.Find(id);
            if (job is null)
            {
                return "Job nicht gefunden.";
            }

            if (job.State != JobState.AwaitingApproval)
            {
                return "Dieser Job wartet nicht auf Freigabe.";
            }

            if (IsBeingPlayed(job))
            {
                return "Die Datei wird gerade abgespielt. Bitte nach der Wiedergabe freigeben.";
            }

            if (job.PendingRename is not null)
            {
                return "Die Dateien werden gerade umbenannt. Bitte gleich noch einmal versuchen.";
            }

            if (string.Equals(Path.GetFullPath(job.SourcePath), Path.GetFullPath(job.TargetPath), StringComparison.Ordinal))
            {
                // Darf nie vorkommen; trotzdem absichern: sonst wuerde die einzige Datei geloescht.
                return "Original und neue Datei sind dieselbe Datei. Es wird nichts gelöscht.";
            }

            // Vor dem Loeschen des Originals noch einmal sicherstellen, dass die neue Datei da und abspielbar ist.
            var target = new FileInfo(job.TargetPath);
            var problem = !target.Exists
                ? "Die umgewandelte Datei " + target.Name + " fehlt."
                : await VerifyAsync(job, job.TargetPath, token).ConfigureAwait(false);
            if (problem is not null)
            {
                return "Freigabe nicht möglich: " + problem + " Das Original bleibt erhalten.";
            }

            var userStates = CaptureUserStates(job.Name, job.SourcePath, job.TargetPath);

            try
            {
                var source = new FileInfo(job.SourcePath);
                if (source.Exists)
                {
                    if (source.LinkTarget is not null)
                    {
                        return "Das Original ist inzwischen ein Symlink und wird nicht gelöscht.";
                    }

                    source.Delete();
                }

                foreach (var sidecar in job.Sidecars)
                {
                    DeleteIfExists(sidecar.Original);
                }

                // NFO, Bilder und Extras mit dem alten Dateinamen gehoeren jetzt zur neuen Datei.
                RenameCompanions(job, job.SourcePath, job.TargetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Das Original konnte nicht gelöscht werden: " + ex.Message;
            }

            _store.Update(job, j =>
            {
                j.State = JobState.Done;
                j.Finished = DateTime.UtcNow;
                j.PendingUserStates = userStates;
                j.Message = (automatic ? "Automatisch freigegeben" : "Freigegeben") + ", Original gelöscht. Neue Datei: " + Path.GetFileName(j.TargetPath);
            });
            _logger.LogInformation("Codec Converter: {Name} {Mode} freigegeben, Original {Path} gelöscht", job.Name, automatic ? "automatisch" : "von Hand", job.SourcePath);
            _log.Add("FREIGEGEBEN " + job.Name + (automatic ? " (automatisch nach Fristablauf)" : string.Empty) + ": Original " + Path.GetFileName(job.SourcePath) + " gelöscht.");
            RefreshLibrary(job);
            return null;
        }
        finally
        {
            _approvalLock.Release();
        }
    }

    /// <summary>
    /// Merkt sich Gesehen-Status, Fortschritt und Favorit aller Benutzer fuer das Original.
    /// </summary>
    /// <summary>
    /// Liest den Stand aus mehreren Dateien und fuehrt ihn je Benutzer zusammen. Bei Versionen haengt der Stand
    /// an der Hauptversion, und welche Datei das ist, legt Jellyfin selbst fest.
    /// </summary>
    private List<UserState> CaptureUserStates(string name, params string[] paths)
    {
        var result = new Dictionary<Guid, UserState>();
        try
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var item = _libraryManager.FindByPath(path, false);
                if (item is null)
                {
                    continue;
                }

                foreach (var user in _userManager.GetUsers())
                {
                    var data = _userDataManager.GetUserData(user, item);
                    if (data is null || (!data.Played && !data.IsFavorite && data.PlaybackPositionTicks == 0 && data.PlayCount == 0))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(user.Id, out var state))
                    {
                        result[user.Id] = state = new UserState { UserId = user.Id };
                    }

                    state.Played |= data.Played;
                    state.IsFavorite |= data.IsFavorite;
                    state.PlaybackPositionTicks = Math.Max(state.PlaybackPositionTicks, data.PlaybackPositionTicks);
                    state.PlayCount = Math.Max(state.PlayCount, data.PlayCount);
                    if (data.LastPlayedDate > state.LastPlayedDate || state.LastPlayedDate is null)
                    {
                        state.LastPlayedDate = data.LastPlayedDate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Der Gesehen-Status ist kein Grund, die Freigabe scheitern zu lassen.
            _logger.LogWarning(ex, "Codec Converter: Gesehen-Status von {Name} konnte nicht gelesen werden", name);
        }

        return result.Values.ToList();
    }

    /// <summary>
    /// Uebertraegt den gemerkten Stand auf die neue Datei, sobald Jellyfin das geloeschte Original aus der
    /// Bibliothek genommen hat. Vorher wuerde Jellyfin den Stand beim Umbau der Versionen wieder verwerfen.
    /// </summary>
    private void RestoreUserStates()
    {
        if (DateTime.UtcNow - _lastUserStateRun < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastUserStateRun = DateTime.UtcNow;
        RestoreRenameStates();
        foreach (var id in _store.IdsInState(JobState.Done).Concat(_store.IdsInState(JobState.Rejected)))
        {
            var job = _store.Find(id);
            if (job is null || job.PendingUserStates.Count == 0)
            {
                continue;
            }

            // Freigegeben: der Stand geht auf die neue Datei. Verworfen: zurueck auf das Original.
            var keep = job.State == JobState.Done ? job.TargetPath : job.SourcePath;
            var gone = job.State == JobState.Done ? job.SourcePath : job.TargetPath;
            try
            {
                if (job.Finished < DateTime.UtcNow.AddDays(-1))
                {
                    // Die Datei ist nie in der Bibliothek aufgetaucht: aufgeben.
                    _store.Update(job, j => j.PendingUserStates = new List<UserState>());
                    continue;
                }

                var item = _libraryManager.FindByPath(keep, false);
                if (item is null || _libraryManager.FindByPath(gone, false) is not null)
                {
                    continue;
                }

                SaveUserStates(job.PendingUserStates, item);
                _store.Update(job, j => j.PendingUserStates = new List<UserState>());
                _logger.LogInformation("Codec Converter: Gesehen-Status von {Name} auf {File} übertragen", job.Name, Path.GetFileName(keep));
                _log.Add("Gesehen-Status von " + job.Name + " auf " + Path.GetFileName(keep) + " übertragen.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codec Converter: Gesehen-Status von {Name} konnte nicht übertragen werden", job.Name);
                _store.Update(job, j => j.PendingUserStates = new List<UserState>());
            }
        }
    }

    /// <summary>
    /// Verwirft eine umgewandelte Datei: die neue Datei wird geloescht, das Original bleibt.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <param name="token">Abbruch-Token.</param>
    /// <returns>Fehlertext oder null.</returns>
    public async Task<string?> RejectAsync(string id, CancellationToken token)
    {
        await _approvalLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var job = _store.Find(id);
            if (job is null)
            {
                return "Job nicht gefunden.";
            }

            if (job.State != JobState.AwaitingApproval)
            {
                return "Dieser Job wartet nicht auf Freigabe.";
            }

            if (IsBeingPlayed(job))
            {
                return "Die Datei wird gerade abgespielt. Bitte nach der Wiedergabe verwerfen.";
            }

            if (job.PendingRename is not null)
            {
                return "Die Dateien werden gerade umbenannt. Bitte gleich noch einmal versuchen.";
            }

            if (!File.Exists(job.SourcePath))
            {
                return "Das Original fehlt. Die umgewandelte Datei wird deshalb nicht gelöscht.";
            }

            // War die neue Datei die Hauptversion, haengt der Gesehen-Status an ihr. Merken fuer das Original.
            var userStates = CaptureUserStates(job.Name, job.SourcePath, job.TargetPath);

            try
            {
                var target = new FileInfo(job.TargetPath);
                if (target.Exists || target.LinkTarget is not null)
                {
                    target.Delete();
                }

                foreach (var sidecar in job.Sidecars)
                {
                    DeleteIfExists(sidecar.Copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Die umgewandelte Datei konnte nicht gelöscht werden: " + ex.Message;
            }

            _store.Update(job, j =>
            {
                j.State = JobState.Rejected;
                j.Finished = DateTime.UtcNow;
                j.PendingUserStates = userStates;
                j.Message = "Verworfen, das Original bleibt" + (j.OriginalPath is not null ? " (unter dem Namen " + Path.GetFileName(j.SourcePath) + ")." : ".");
            });
            _logger.LogInformation("Codec Converter: {Name} verworfen, {Path} gelöscht", job.Name, job.TargetPath);
            _log.Add("VERWORFEN " + job.Name + ": neue Datei " + Path.GetFileName(job.TargetPath) + " gelöscht, Original bleibt.");
            RefreshLibrary(job);
            return null;
        }
        finally
        {
            _approvalLock.Release();
        }
    }

    /// <summary>
    /// Gibt alle wartenden Jobs frei.
    /// </summary>
    /// <param name="token">Abbruch-Token.</param>
    /// <returns>Anzahl der Freigaben und die Fehlertexte.</returns>
    public async Task<(int Approved, List<string> Errors)> ApproveAllAsync(CancellationToken token)
    {
        var approved = 0;
        var errors = new List<string>();
        foreach (var id in _store.IdsInState(JobState.AwaitingApproval))
        {
            var error = await ApproveAsync(id, false, token).ConfigureAwait(false);
            if (error is null)
            {
                approved++;
            }
            else
            {
                errors.Add((_store.Find(id)?.Name ?? id) + ": " + error);
            }
        }

        return (approved, errors);
    }

    /// <summary>
    /// Gibt Jobs frei, deren Frist abgelaufen ist.
    /// </summary>
    private async Task AutoApproveAsync(CancellationToken token)
    {
        // Kurz nach dem Serverstart ist Jellyfins ffmpeg noch nicht bereit, die neue Datei liesse sich nicht pruefen.
        if (DateTime.UtcNow - _lastAutoApprove < TimeSpan.FromMinutes(10) || string.IsNullOrEmpty(_mediaEncoder.EncoderPath) || string.IsNullOrEmpty(_mediaEncoder.ProbePath))
        {
            return;
        }

        _lastAutoApprove = DateTime.UtcNow;
        var days = AutoApproveDays;
        if (days <= 0)
        {
            return;
        }

        foreach (var id in _store.IdsInState(JobState.AwaitingApproval))
        {
            var job = _store.Find(id);
            // Umwandlungen mit Lesefehlern in der Quelle gibt nur ein Mensch frei.
            if (job?.Converted is null || job.SourceErrors > 0 || DateTime.UtcNow - job.Converted.Value < TimeSpan.FromDays(days))
            {
                continue;
            }

            var error = await ApproveAsync(id, true, token).ConfigureAwait(false);
            if (error is not null)
            {
                // Bleibt wartend und wird beim naechsten Durchlauf erneut versucht.
                _logger.LogInformation("Codec Converter: automatische Freigabe von {Name} verschoben: {Error}", job.Name, error);
            }
        }
    }

    private void RefreshLibrary(Job job)
    {
        try
        {
            _index.Invalidate();
            _libraryMonitor.ReportFileSystemChanged(job.TargetPath);
            _libraryMonitor.ReportFileSystemChanged(job.SourcePath);
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
        foreach (var snapshot in _store.Snapshot().Where(j => j.PendingRename is not null))
        {
            var job = _store.Find(snapshot.Id);
            try
            {
                if (job is not null)
                {
                    CompleteRename(job);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Codec Converter: begonnene Umbenennung von {Name} ließ sich nicht abschließen", snapshot.Name);
                if (job is not null)
                {
                    _store.Update(job, j => j.Message = "Eine Umbenennung wurde durch einen Absturz unterbrochen und ließ sich nicht abschließen: " + ex.Message + " Bitte den Ordner von Hand prüfen.");
                }
            }
        }

        foreach (var snapshot in _store.Snapshot())
        {
            var job = _store.Find(snapshot.Id);
            if (job is null || job.State is not (JobState.Running or JobState.Paused or JobState.Finalizing))
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

        if (tempExists && !sourceExists && job.BackupFile is not null && File.Exists(job.BackupFile))
        {
            // Stand aus Version 1.0: das Original lag schon im Sicherungsordner, nur das Umbenennen fehlte.
            File.Move(job.TempPath, job.TargetPath, false);
            var legacySize = new FileInfo(job.TargetPath).Length;
            _store.Update(job, j =>
            {
                j.State = JobState.Done;
                j.ResultSize = legacySize;
                j.Finished = DateTime.UtcNow;
                j.Message = "Original gesichert unter " + j.BackupFile;
            });
            return;
        }

        if (tempExists)
        {
            // Die neue Datei war noch nicht an ihrem Platz: verwerfen und neu umwandeln.
            File.Delete(job.TempPath);
            _store.Update(job, j =>
            {
                j.State = sourceExists ? JobState.Queued : JobState.Failed;
                j.Progress = 0;
                j.Message = sourceExists ? null : "Nach einem Absturz fehlt die Quelldatei. Bitte von Hand prüfen.";
            });
            return;
        }

        if (File.Exists(job.TargetPath) && sourceExists)
        {
            // Die neue Datei liegt schon neben dem Original, nur der Vermerk fehlte.
            var size = new FileInfo(job.TargetPath).Length;
            _store.Update(job, j =>
            {
                j.State = JobState.AwaitingApproval;
                j.ResultSize = size;
                j.Finished = DateTime.UtcNow;
                j.Converted = DateTime.UtcNow;
                j.Message = AwaitingMessage(j);
            });
            return;
        }

        _store.Update(job, j =>
        {
            j.State = JobState.Failed;
            j.Finished = DateTime.UtcNow;
            j.Message = "Nach einem Absturz ist der Stand unklar. Bitte von Hand prüfen.";
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
