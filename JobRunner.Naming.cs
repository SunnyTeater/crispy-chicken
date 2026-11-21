using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Benennung nach Jellyfins Versionsregel: neue Datei "Ordnername-HEVC", und wo das Original nicht zur Regel
/// passt, auch das Original ("Ordnername-H264"). Nur so zeigt Jellyfin beide Dateien als Versionen eines Films.
/// </summary>
public sealed partial class JobRunner
{
    // 1: Filme nach Ordnernamen (1.3). 2: zusaetzlich Folgen nach Staffel/Folge und Fassungen im Namen (1.4).
    private const int CurrentNamingVersion = 2;

    private DateTime _lastNamingRun = DateTime.MinValue;

    /// <summary>
    /// Ergebnis der Namensplanung.
    /// </summary>
    /// <param name="TargetPath">Pfad der neuen Datei.</param>
    /// <param name="OriginalNewPath">Neuer Pfad des Originals, wenn es umbenannt werden muss.</param>
    /// <param name="Note">Hinweis, warum keine Versionen moeglich sind, sonst null.</param>
    /// <param name="Versioned">Ob die Namen der Versionsregel folgen (nur bei Filmen im eigenen Ordner).</param>
    internal sealed record NamingPlan(string TargetPath, string? OriginalNewPath, string? Note, bool Versioned);

    /// <summary>
    /// Staffel und Folge laut Jellyfin.
    /// </summary>
    /// <param name="Season">Staffel.</param>
    /// <param name="Number">Folge.</param>
    /// <param name="End">Letzte Folge bei Doppelfolgen.</param>
    /// <param name="SeriesFolder">Name des Serienordners.</param>
    internal sealed record EpisodeNumbers(int Season, int Number, int? End, string SeriesFolder);

    /// <summary>
    /// Plant die Namen von neuer Datei und Original.
    /// </summary>
    /// <param name="sourcePath">Das Original.</param>
    /// <param name="sourceCodec">Codec des Originals.</param>
    /// <param name="targetCodec">Zielcodec.</param>
    /// <param name="isMovie">Ob es ein Film ist (Folgen fasst Jellyfin nach Staffel und Folge zusammen, nicht nach Namen).</param>
    /// <param name="episode">Staffel, Folge und Serienordner laut Jellyfin, bei Folgen.</param>
    /// <param name="ownTargetPath">Die schon vorhandene neue Datei dieses Jobs, falls umbenannt wird.</param>
    /// <param name="reserved">Pfade, die andere Jobs fuer sich vorgesehen haben.</param>
    /// <returns>Die Namen.</returns>
    internal NamingPlan PlanNames(string sourcePath, string sourceCodec, string targetCodec, bool isMovie, EpisodeNumbers? episode, string? ownTargetPath, ICollection<string> reserved)
    {
        // Eine Fassung im Originalnamen ("Extended") bleibt in beiden Namen erhalten: "Ordner-Extended-HEVC".
        var edition = NamingLabels.Edition(Path.GetFileNameWithoutExtension(sourcePath));
        var sourceLabel = NamingLabels.Label(edition, sourceCodec);
        var targetLabel = NamingLabels.Label(edition, targetCodec);
        var extension = EncoderCatalog.GetContainer(sourcePath, targetCodec).Extension;
        var directory = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var takenBySource = new List<string>(reserved) { sourcePath };

        NamingPlan Fallback(string? note)
        {
            // Folgen und Filme ohne eigenen Ordner: Jellyfin fasst hier nicht nach dem Ordnernamen zusammen.
            var target = FreePath(directory, EncoderCatalog.BuildTargetName(baseName, targetCodec), extension, ownTargetPath, takenBySource, baseName);
            return new NamingPlan(target, null, note, false);
        }

        if (!isMovie)
        {
            return PlanEpisode(sourcePath, sourceLabel, targetLabel, extension, episode, ownTargetPath, reserved, takenBySource, Fallback);
        }

        var folder = Path.GetFileName(directory);
        if (folder.Length <= 1 || IsLibraryRoot(directory))
        {
            return Fallback("Der Film liegt nicht in einem eigenen Ordner. Jellyfin zeigt Versionen nur, wenn jeder Film einen eigenen Ordner hat.");
        }

        // Alle anderen Videodateien im Ordner muessen ebenfalls zur Regel passen, sonst fasst Jellyfin nichts zusammen.
        var others = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(file, sourcePath, StringComparison.Ordinal) || string.Equals(file, ownTargetPath, StringComparison.Ordinal)
                || !JellyfinNaming.IsVideoFile(file) || JellyfinNaming.IsExtra(name))
            {
                continue;
            }

            others.Add(name);
        }

        var originalBase = baseName;
        string? originalNewPath = null;
        if (!JellyfinNaming.IsEligible(folder, baseName))
        {
            var takenByTarget = new List<string>(reserved);
            if (ownTargetPath is not null)
            {
                takenByTarget.Add(ownTargetPath);
            }

            originalNewPath = FreePath(directory, folder + "-" + sourceLabel, Path.GetExtension(sourcePath), null, takenByTarget, null);
            originalBase = Path.GetFileNameWithoutExtension(originalNewPath);
        }

        var taken = new List<string>(takenBySource);
        if (originalNewPath is not null)
        {
            taken.Add(originalNewPath);
        }

        var targetPath = FreePath(directory, folder + "-" + targetLabel, extension, ownTargetPath, taken, originalBase);
        var names = new List<string>(others) { originalBase, Path.GetFileNameWithoutExtension(targetPath) };
        if (JellyfinNaming.WouldGroup(folder, names))
        {
            return new NamingPlan(targetPath, originalNewPath, null, true);
        }

        return Fallback(others.Any(n => !JellyfinNaming.IsEligible(folder, n))
            ? "Im Ordner liegen weitere Videodateien, die nicht zum Ordnernamen passen (etwa ein anderer Film). Jellyfin kann hier keine Versionen bilden, das Original bleibt unverändert."
            : "Die Dateien im Ordner ergeben für Jellyfin unterschiedliche Jahreszahlen. Jellyfin kann hier keine Versionen bilden, das Original bleibt unverändert.");
    }

    /// <summary>
    /// Folgen: Jellyfin fasst nur Dateien zusammen, deren Namen Staffel und Folge eindeutig tragen ("S01E05", "1x05").
    /// Traegt das Original sie, bleibt es und die neue Datei folgt seiner Schreibweise. Sonst bekommen beide den
    /// Standard "Serienordner S01E05-H264" bzw. "-HEVC".
    /// </summary>
    private NamingPlan PlanEpisode(string sourcePath, string sourceLabel, string targetLabel, string extension, EpisodeNumbers? episode, string? ownTargetPath, ICollection<string> reserved, List<string> takenBySource, Func<string?, NamingPlan> fallback)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var key = EpisodeNaming.VersionKey(sourcePath);
        if (key is not null)
        {
            var plan = fallback(null);
            if (string.Equals(EpisodeNaming.VersionKey(plan.TargetPath), key, StringComparison.Ordinal))
            {
                return plan with { Versioned = true };
            }

            var labelled = FreePath(directory, baseName + "-" + targetLabel, extension, ownTargetPath, takenBySource, baseName);
            return string.Equals(EpisodeNaming.VersionKey(labelled), key, StringComparison.Ordinal)
                ? new NamingPlan(labelled, null, null, true)
                : fallback("Jellyfin würde die neue Datei nicht als dieselbe Folge erkennen. Sie erscheint bis zur Freigabe als eigene Folge.");
        }

        if (episode is null || string.IsNullOrWhiteSpace(episode.SeriesFolder))
        {
            return fallback("Staffel und Folge sind unbekannt. Jellyfin kann hier keine Versionen bilden, das Original bleibt unverändert.");
        }

        var standard = episode.SeriesFolder + FormattableString.Invariant($" S{episode.Season:D2}E{episode.Number:D2}")
            + (episode.End > episode.Number ? FormattableString.Invariant($"-E{episode.End:D2}") : string.Empty);
        var expected = FormattableString.Invariant($"S{episode.Season}E{episode.Number}");
        var takenByTarget = new List<string>(reserved);
        if (ownTargetPath is not null)
        {
            takenByTarget.Add(ownTargetPath);
        }

        var originalNewPath = FreePath(directory, standard + "-" + sourceLabel, Path.GetExtension(sourcePath), null, takenByTarget, null);
        var taken = new List<string>(takenBySource) { originalNewPath };
        var targetPath = FreePath(directory, standard + "-" + targetLabel, extension, ownTargetPath, taken, Path.GetFileNameWithoutExtension(originalNewPath));
        if (string.Equals(EpisodeNaming.VersionKey(originalNewPath), expected, StringComparison.Ordinal)
            && string.Equals(EpisodeNaming.VersionKey(targetPath), expected, StringComparison.Ordinal))
        {
            return new NamingPlan(targetPath, originalNewPath, null, true);
        }

        return fallback("Für diese Folge ließ sich kein eindeutiger Name bilden. Das Original bleibt unverändert.");
    }

    /// <summary>
    /// Staffel, Folge und Name des Serienordners laut Jellyfin, oder null.
    /// </summary>
    private EpisodeNumbers? EpisodeInfo(Guid itemId, string path)
    {
        var item = (_libraryManager.GetItemById(itemId) as Episode) ?? (_libraryManager.FindByPath(path, false) as Episode);
        var season = item?.ParentIndexNumber ?? item?.Season?.IndexNumber;
        if (item?.IndexNumber is not int number || season is not int s)
        {
            return null;
        }

        var seriesPath = item.Series?.Path;
        string? folder;
        if (!string.IsNullOrEmpty(seriesPath))
        {
            folder = Path.GetFileName(seriesPath.TrimEnd(Path.DirectorySeparatorChar));
        }
        else
        {
            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(directory);
            folder = _seasonFolder.IsMatch(name) ? Path.GetFileName(Path.GetDirectoryName(directory)) : name;
        }

        return new EpisodeNumbers(s, number, item.IndexNumberEnd, folder ?? string.Empty);
    }

    private static readonly System.Text.RegularExpressions.Regex _seasonFolder = new(@"^(season|staffel|s)[ ._-]*\d+$|^specials$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Ein freier Dateiname: base, base-2, base-3 ... Frei heisst: nicht von anderen belegt, nicht vorhanden
    /// (ausser es ist die Datei, die ohnehin diesen Namen bekommt) und nicht derselbe Grundname wie das Original,
    /// sonst wuerden sich Untertitel beider Dateien in die Quere kommen.
    /// </summary>
    private static string FreePath(string directory, string baseName, string extension, string? ownPath, ICollection<string> taken, string? otherBase)
    {
        for (var i = 1; ; i++)
        {
            var candidateBase = i == 1 ? baseName : baseName + "-" + i;
            var candidate = Path.Combine(directory, candidateBase + extension);
            if (string.Equals(candidateBase, otherBase, StringComparison.OrdinalIgnoreCase) || taken.Contains(candidate))
            {
                continue;
            }

            if (string.Equals(candidate, ownPath, StringComparison.Ordinal) || !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private bool IsLibraryRoot(string directory)
    {
        var full = WithSeparator(Path.GetFullPath(directory));
        return _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Any(l => string.Equals(WithSeparator(Path.GetFullPath(l)), full, StringComparison.Ordinal));
    }

    /// <summary>
    /// Ob der Job einen Film betrifft. Bei Jobs aus aelteren Versionen fehlt die Angabe; dann fragt das Plugin Jellyfin.
    /// </summary>
    private bool IsMovie(Job job)
    {
        if (job.ItemType is not null)
        {
            return job.ItemType == "Movie";
        }

        var item = _libraryManager.GetItemById(job.ItemId) ?? _libraryManager.FindByPath(job.SourcePath, false);
        var type = item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            _ => null
        };
        if (type is not null)
        {
            _store.Update(job, j => j.ItemType = type);
        }

        return type == "Movie";
    }

    private HashSet<string> ReservedTargets(string exceptJobId)
        => _store.Snapshot()
            .Where(j => !string.Equals(j.Id, exceptJobId, StringComparison.Ordinal) && JobStore.IsOpen(j))
            .SelectMany(j => new[] { j.TargetPath, j.SourcePath })
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Bringt wartende Jobs auf die Benennung nach Jellyfins Versionsregel: Jobs aus aelteren Versionen und
    /// Jobs, deren Original beim Fertigstellen gerade abgespielt wurde.
    /// </summary>
    private async Task FixNamingAsync(CancellationToken token)
    {
        // Erst wenn Jellyfin fertig gestartet ist: vorher fehlen Bibliothek und Sitzungen.
        if (DateTime.UtcNow - _lastNamingRun < TimeSpan.FromSeconds(30)
            || DateTime.UtcNow - _startTime < TimeSpan.FromSeconds(60)
            || string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
        {
            return;
        }

        _lastNamingRun = DateTime.UtcNow;
        foreach (var id in _store.IdsInState(JobState.AwaitingApproval))
        {
            var snapshot = _store.Find(id);
            if (snapshot is null || (snapshot.NamingApplied && snapshot.NamingVersion >= CurrentNamingVersion))
            {
                continue;
            }

            await _approvalLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var job = _store.Find(id);
                if (job is null || job.State != JobState.AwaitingApproval || (job.NamingApplied && job.NamingVersion >= CurrentNamingVersion) || IsBeingPlayed(job)
                    || !File.Exists(job.SourcePath) || !File.Exists(job.TargetPath))
                {
                    continue;
                }

                var movie = IsMovie(job);
                var plan = PlanNames(job.SourcePath, job.SourceCodec, job.TargetCodec, movie, movie ? null : EpisodeInfo(job.ItemId, job.SourcePath), job.TargetPath, ReservedTargets(job.Id));
                ApplyPlan(job, plan, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Codec Converter: Umbenennen für {Id} fehlgeschlagen, neuer Versuch später", id);
            }
            finally
            {
                _approvalLock.Release();
            }
        }
    }

    /// <summary>
    /// Setzt eine Namensplanung um: speichert sie erst, fuehrt sie dann aus.
    /// </summary>
    /// <param name="job">Der Job.</param>
    /// <param name="plan">Die Planung.</param>
    /// <param name="moveTarget">Ob auch die schon vorhandene neue Datei umbenannt werden darf.</param>
    private void ApplyPlan(Job job, NamingPlan plan, bool moveTarget)
    {
        var rename = new PendingRename();
        if (plan.OriginalNewPath is not null && !string.Equals(plan.OriginalNewPath, job.SourcePath, StringComparison.Ordinal))
        {
            rename.OldSource = job.SourcePath;
            rename.NewSource = plan.OriginalNewPath;
        }

        // Eine schon vorhandene neue Datei nur umbenennen, wenn Jellyfin dadurch Versionen bildet.
        if (moveTarget && plan.Versioned && !string.Equals(plan.TargetPath, job.TargetPath, StringComparison.Ordinal))
        {
            rename.OldTarget = job.TargetPath;
            rename.NewTarget = plan.TargetPath;
        }

        if (rename.NewSource is null && rename.NewTarget is null)
        {
            _store.Update(job, j =>
            {
                j.NamingApplied = true;
                j.NamingVersion = CurrentNamingVersion;
            });
            if (plan.Note is not null)
            {
                _log.Add("Benennung " + job.Name + ": " + plan.Note);
            }

            return;
        }

        if (rename.NewSource is not null && job.RenameUserStates.Count == 0)
        {
            // Der Gesehen-Status haengt an der alten Datei. Merken, bevor Jellyfin sie unter neuem Namen neu anlegt.
            var states = CaptureUserStates(job.Name, job.SourcePath);
            _store.Update(job, j => j.RenameUserStates = states, persist: false);
        }

        _store.Update(job, j => j.PendingRename = rename);
        CompleteRename(job);
    }

    /// <summary>
    /// Fuehrt eine gespeicherte Umbenennung zu Ende. Jeder Schritt prueft, ob er schon erledigt ist,
    /// deshalb darf das nach einem Absturz beliebig oft wiederholt werden.
    /// </summary>
    private void CompleteRename(Job job)
    {
        var rename = job.PendingRename;
        if (rename is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(job.SourcePath)!;
        var notes = new List<string>();

        if (rename.OldTarget is not null && rename.NewTarget is not null)
        {
            MoveOnce(rename.OldTarget, rename.NewTarget);
            var oldBase = Path.GetFileNameWithoutExtension(rename.OldTarget);
            var newBase = Path.GetFileNameWithoutExtension(rename.NewTarget);
            var sidecars = job.Sidecars.Select(s =>
            {
                var name = Path.GetFileName(s.Copy);
                if (!name.StartsWith(oldBase + ".", StringComparison.Ordinal))
                {
                    return s;
                }

                var moved = Path.Combine(Path.GetDirectoryName(s.Copy)!, newBase + name[oldBase.Length..]);
                MoveOnce(s.Copy, moved);
                return new SidecarCopy { Original = s.Original, Copy = moved };
            }).ToList();
            _store.Update(job, j =>
            {
                j.TargetPath = rename.NewTarget;
                j.Sidecars = sidecars;
            });
            notes.Add("neue Datei " + Path.GetFileName(rename.OldTarget) + " → " + Path.GetFileName(rename.NewTarget));
        }

        if (rename.OldSource is not null && rename.NewSource is not null)
        {
            MoveOnce(rename.OldSource, rename.NewSource);
            var moves = RenameCompanions(job, rename.OldSource, rename.NewSource);
            _store.Update(job, j =>
            {
                j.SourcePath = rename.NewSource;
                j.OriginalPath ??= rename.OldSource;
                j.RenamedAt = DateTime.UtcNow;
                j.Sidecars = j.Sidecars.Select(s => moves.TryGetValue(s.Original, out var moved)
                    ? new SidecarCopy { Original = moved, Copy = s.Copy }
                    : s).ToList();
            });
            notes.Add("Original " + Path.GetFileName(rename.OldSource) + " → " + Path.GetFileName(rename.NewSource)
                + (moves.Count > 0 ? " (mit " + moves.Count + " Begleitdatei(en))" : string.Empty));
        }

        _store.Update(job, j =>
        {
            j.PendingRename = null;
            j.NamingApplied = true;
            j.NamingVersion = CurrentNamingVersion;
            if (j.State == JobState.AwaitingApproval)
            {
                j.Message = AwaitingMessage(j);
            }
        });

        _logger.LogInformation("Codec Converter: {Name} nach Jellyfins Versionsregel umbenannt: {Notes}", job.Name, string.Join("; ", notes));
        _log.Add("UMBENANNT " + job.Name + ": " + string.Join("; ", notes) + ". Jellyfin zeigt beide als Versionen.");
        try
        {
            _index.Invalidate();
            _libraryMonitor.ReportFileSystemChanged(directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codec Converter: Bibliothek konnte nicht aktualisiert werden");
        }
    }

    /// <summary>
    /// Benennt die Begleitdateien des Originals mit um: Untertitel, NFO, Bilder, Extras wie "-trailer",
    /// also alles, dessen Name mit dem alten Dateinamen und einem Trenner beginnt.
    /// </summary>
    /// <returns>Alte und neue Pfade.</returns>
    private Dictionary<string, string> RenameCompanions(Job job, string oldSource, string newSource)
    {
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(oldSource)!;
        var oldBase = Path.GetFileNameWithoutExtension(oldSource);
        var newBase = Path.GetFileNameWithoutExtension(newSource);
        var own = new HashSet<string>(StringComparer.Ordinal) { oldSource, newSource, job.TargetPath, job.TempPath };
        foreach (var sidecar in job.Sidecars)
        {
            own.Add(sidecar.Copy);
        }

        var files = Directory.EnumerateFiles(directory).ToList();

        // Eine Begleitdatei gehoert zu dem Video mit dem laengsten passenden Namen: "Film-HEVC.de.srt" gehoert zu
        // "Film-HEVC", nicht zu "Film". Nur was zum Original gehoert, wandert mit.
        var videoBases = files
            .Where(f => JellyfinNaming.IsVideoFile(f) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(f)))
            .Select(Path.GetFileNameWithoutExtension)
            .Append(oldBase)
            .Append(Path.GetFileNameWithoutExtension(job.TargetPath))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? Owner(string name) => videoBases
            .Where(b => name.Length > b!.Length && name.StartsWith(b, StringComparison.Ordinal) && name[b.Length] is '.' or '-' or '_')
            .OrderByDescending(b => b!.Length)
            .FirstOrDefault();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (own.Contains(file)
                || !string.Equals(Owner(name), oldBase, StringComparison.Ordinal)
                || new FileInfo(file).LinkTarget is not null)
            {
                continue;
            }

            // Andere Videos bleiben, wie sie sind, ausser Extras, die zum Original gehoeren ("...-trailer.mkv").
            if (JellyfinNaming.IsVideoFile(file) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }

            var target = Path.Combine(directory, newBase + name[oldBase.Length..]);
            try
            {
                if (!File.Exists(target))
                {
                    File.Move(file, target, false);
                    moves[file] = target;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Eine Begleitdatei mit altem Namen ist kein Grund, die Umbenennung abzubrechen.
                _logger.LogWarning(ex, "Codec Converter: {File} konnte nicht umbenannt werden", name);
            }
        }

        return moves;
    }

    // Verschiebt einmal: ist es schon erledigt (alte Datei weg, neue da), passiert nichts.
    private static void MoveOnce(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to))
        {
            File.Move(from, to, false);
        }
        else if (!File.Exists(to))
        {
            throw new IOException("Weder " + Path.GetFileName(from) + " noch " + Path.GetFileName(to) + " ist vorhanden.");
        }
    }

    private static string AwaitingMessage(Job job)
        => "Neue Datei: " + Path.GetFileName(job.TargetPath) + "."
            + (job.OriginalPath is not null
                ? " Original umbenannt in " + Path.GetFileName(job.SourcePath) + " (vorher " + Path.GetFileName(job.OriginalPath) + "), damit Jellyfin beide als Versionen zeigt."
                : string.Empty)
            + " Das Original bleibt bis zur Freigabe erhalten."
            + (job.SourceErrors > 0
                ? " Achtung: ffmpeg meldete " + job.SourceErrors + " Fehler beim Lesen der Quelle, sie ist vermutlich stellenweise beschädigt. Bitte die neue Datei vor der Freigabe ansehen; automatisch wird sie nicht freigegeben."
                : string.Empty);

    /// <summary>
    /// Uebertraegt den Gesehen-Status des umbenannten Originals auf den Film, sobald Jellyfin die Datei unter
    /// neuem Namen eingelesen hat.
    /// </summary>
    private void RestoreRenameStates()
    {
        foreach (var snapshot in _store.Snapshot().Where(j => j.RenameUserStates.Count > 0 && j.PendingRename is null && j.OriginalPath is not null))
        {
            var job = _store.Find(snapshot.Id);
            if (job is null)
            {
                continue;
            }

            try
            {
                if ((job.RenamedAt ?? DateTime.MinValue) < DateTime.UtcNow.AddDays(-1))
                {
                    _store.Update(job, j => j.RenameUserStates = new List<UserState>());
                    continue;
                }

                if (_libraryManager.FindByPath(job.OriginalPath!, false) is not null)
                {
                    continue;
                }

                // Nach einer Freigabe ist das Original schon weg: dann bekommt die neue Datei den Stand.
                var item = _libraryManager.FindByPath(job.SourcePath, false) ?? _libraryManager.FindByPath(job.TargetPath, false);
                if (item is null)
                {
                    continue;
                }

                var targets = new List<BaseItem> { item };
                if (item is Video video && video.PrimaryVersionId.HasValue && _libraryManager.GetItemById(video.PrimaryVersionId.Value) is { } primary)
                {
                    // Jellyfin zeigt den Film mit der Hauptversion an; der Gesehen-Status gehoert dorthin.
                    targets.Add(primary);
                }

                foreach (var target in targets)
                {
                    SaveUserStates(job.RenameUserStates, target);
                }

                _store.Update(job, j => j.RenameUserStates = new List<UserState>());
                _log.Add("Gesehen-Status von " + job.Name + " auf den umbenannten Film übertragen.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codec Converter: Gesehen-Status von {Name} konnte nach dem Umbenennen nicht übertragen werden", job.Name);
                _store.Update(job, j => j.RenameUserStates = new List<UserState>());
            }
        }
    }

    private void SaveUserStates(IEnumerable<UserState> states, BaseItem item)
    {
        foreach (var state in states)
        {
            var user = _userManager.GetUserById(state.UserId);
            if (user is null)
            {
                continue;
            }

            _userDataManager.SaveUserData(
                user,
                item,
                new UpdateUserItemDataDto
                {
                    Played = state.Played,
                    IsFavorite = state.IsFavorite,
                    PlaybackPositionTicks = state.PlaybackPositionTicks,
                    PlayCount = state.PlayCount,
                    LastPlayedDate = state.LastPlayedDate
                },
                UserDataSaveReason.Import);
        }
    }
}
