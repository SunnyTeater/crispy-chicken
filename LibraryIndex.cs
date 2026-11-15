using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Liest Filme und Folgen samt Codec-Daten aus der Jellyfin-Bibliothek.
/// </summary>
public sealed class LibraryIndex
{
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly object _lock = new();
    private List<MediaEntry>? _cache;
    private DateTime _cacheTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Der Bibliotheksmanager.</param>
    public LibraryIndex(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Liefert alle Videodateien. Das Ergebnis wird einige Minuten zwischengespeichert.
    /// </summary>
    /// <param name="refresh">Zwischenspeicher verwerfen.</param>
    /// <returns>Die Eintraege.</returns>
    public IReadOnlyList<MediaEntry> GetEntries(bool refresh)
    {
        lock (_lock)
        {
            if (!refresh && _cache is not null && DateTime.UtcNow - _cacheTime < _cacheLifetime)
            {
                return _cache;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true
            });

            var result = new List<MediaEntry>(items.Count);
            var seen = new HashSet<Guid>();
            foreach (var item in items)
            {
                var entry = CreateEntry(item);
                if (entry is null || !seen.Add(item.Id))
                {
                    continue;
                }

                result.Add(entry);

                // Weitere Versionen desselben Films ("Film (2021) - 1080p.mkv") sind eigene Dateien,
                // tauchen in der Abfrage aber nicht als eigene Elemente auf.
                var versions = item is Video video ? _libraryManager.GetLocalAlternateVersionIds(video).ToList() : new List<Guid>();
                if (versions.Count == 0)
                {
                    continue;
                }

                entry.Name = Path.GetFileNameWithoutExtension(entry.FileName);
                entry.Year = null;
                foreach (var versionId in versions)
                {
                    var version = _libraryManager.GetItemById(versionId);
                    var versionEntry = version is null ? null : CreateEntry(version);
                    if (versionEntry is not null && seen.Add(versionId))
                    {
                        versionEntry.Name = Path.GetFileNameWithoutExtension(versionEntry.FileName);
                        versionEntry.Year = null;
                        result.Add(versionEntry);
                    }
                }
            }

            _cache = result;
            _cacheTime = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    /// Verwirft den Zwischenspeicher.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = null;
        }
    }

    /// <summary>
    /// Liest einen einzelnen Eintrag frisch aus der Bibliothek.
    /// </summary>
    /// <param name="id">Die Item-Id.</param>
    /// <param name="path">Der Dateipfad des Items.</param>
    /// <returns>Der Eintrag oder null.</returns>
    public MediaEntry? GetEntry(Guid id, out string? path)
    {
        var item = _libraryManager.GetItemById(id);
        path = item?.Path;
        return item is null ? null : CreateEntry(item);
    }

    private static MediaEntry? CreateEntry(BaseItem item)
    {
        if (item is not Video video || (item is not Movie && item is not Episode))
        {
            return null;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var stream = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var entry = new MediaEntry
        {
            Id = item.Id,
            Type = item is Movie ? "Movie" : "Episode",
            Name = item.Name ?? Path.GetFileNameWithoutExtension(item.Path),
            Year = item.ProductionYear,
            Codec = (stream?.Codec ?? string.Empty).ToLowerInvariant(),
            Width = stream?.Width ?? 0,
            Height = stream?.Height ?? 0,
            Fps = stream?.RealFrameRate ?? stream?.AverageFrameRate ?? 0,
            VideoBitrate = stream?.BitRate ?? 0,
            Size = item.Size ?? 0,
            DurationSeconds = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0,
            Container = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
            FileName = Path.GetFileName(item.Path)
        };

        if (item is Episode episode)
        {
            entry.SeriesId = episode.SeriesId;
            entry.SeriesName = episode.SeriesName ?? "Unbekannte Serie";
            entry.Season = episode.ParentIndexNumber;
            entry.Episode = episode.IndexNumber;
        }

        if (stream is not null)
        {
            entry.VideoRange = stream.GetVideoColorRange().VideoRangeType.ToString();
        }

        entry.Blocked = GetBlockReason(video, stream, entry);
        return entry;
    }

    private static string? GetBlockReason(Video video, MediaStream? stream, MediaEntry entry)
    {
        if (!video.IsFileProtocol || video.IsShortcut)
        {
            return "Keine lokale Videodatei";
        }

        if (video.VideoType != VideoType.VideoFile || video.IsPlaceHolder)
        {
            return "Disc-Ordner oder Image wird nicht unterstützt";
        }

        if (stream is null || entry.Codec.Length == 0)
        {
            return "Keine Videospur erkannt";
        }

        if (entry.DurationSeconds <= 0)
        {
            return "Laufzeit unbekannt";
        }

        if (entry.VideoRange.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase))
        {
            return "Dolby Vision ginge bei der Umwandlung verloren";
        }

        return null;
    }
}
