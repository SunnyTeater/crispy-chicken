using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Ein Verweis einer Playlist oder Sammlung auf einen Eintrag, der umbenannt wird.
/// </summary>
public sealed class LinkEntry
{
    /// <summary>Gets or sets die Art: "Playlist" oder "Collection".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets die Playlist bzw. Sammlung.</summary>
    public Guid ContainerId { get; set; }

    /// <summary>Gets or sets den Besitzer der Playlist.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Gets or sets die Position in der Playlist.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets die Eintraege direkt davor (naechster zuerst), um die Position wiederzufinden.</summary>
    public List<Guid> Before { get; set; } = new();

    /// <summary>Gets or sets die Eintraege direkt danach (naechster zuerst).</summary>
    public List<Guid> After { get; set; } = new();

    /// <summary>Gets or sets den bisherigen Eintrag.</summary>
    public Guid OldItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether der Verweis uebertragen ist.</summary>
    public bool Done { get; set; }

    /// <summary>Kopie fuer die Bearbeitung ausserhalb des Speichers.</summary>
    /// <returns>Die Kopie.</returns>
    public LinkEntry Clone() => (LinkEntry)MemberwiseClone();
}

/// <summary>
/// Jellyfin verweist in Playlists und Sammlungen nur ueber die Eintrags-ID. Beim Umbenennen legt Jellyfin die
/// Datei als neuen Eintrag an, der alte Verweis zeigt ins Leere und der Film verschwindet aus Playlist und
/// Sammlung. Dieser Baustein merkt sich die Verweise vorher und setzt danach den neuen Eintrag an dieselbe Stelle.
/// </summary>
public sealed class LinkKeeper
{
    private const int Anchors = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<LinkKeeper> _logger;

    // Welcher neue Eintrag einen alten ersetzt hat; hilft, Nachbarn in Playlists wiederzufinden.
    // Wird gespeichert, damit das auch nach einem Neustart von Jellyfin klappt.
    private readonly Dictionary<Guid, Replacement> _replaced = new();
    private readonly object _replacedLock = new();
    private readonly string _replacedFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkKeeper"/> class.
    /// </summary>
    /// <param name="libraryManager">Die Bibliothek.</param>
    /// <param name="playlistManager">Die Playlists.</param>
    /// <param name="collectionManager">Die Sammlungen.</param>
    /// <param name="logger">Der Logger.</param>
    /// <param name="paths">Die Anwendungspfade.</param>
    public LinkKeeper(ILibraryManager libraryManager, IPlaylistManager playlistManager, ICollectionManager collectionManager, ILogger<LinkKeeper> logger, IApplicationPaths paths)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _collectionManager = collectionManager;
        _logger = logger;
        _replacedFile = Path.Combine(paths.DataPath, "codec-converter", "replaced.json");
        LoadReplaced();
    }

    /// <summary>
    /// Liest alle Playlists und Sammlungen einmal und ordnet die Verweise nach Eintrag.
    /// </summary>
    /// <returns>Verweise je Eintrags-ID.</returns>
    public Dictionary<Guid, List<LinkEntry>> Snapshot()
    {
        var result = new Dictionary<Guid, List<LinkEntry>>();
        try
        {
            var containers = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist, BaseItemKind.BoxSet },
                Recursive = true
            });

            foreach (var container in containers)
            {
                // Frisch laden: nur dann sind die Verweise vollstaendig.
                if (_libraryManager.GetItemById(container.Id) is not Folder folder || !folder.LinkedChildrenLoaded)
                {
                    continue;
                }

                var children = folder.LinkedChildren;
                for (var i = 0; i < children.Length; i++)
                {
                    if (children[i].ItemId is not Guid itemId)
                    {
                        continue;
                    }

                    var entry = new LinkEntry
                    {
                        Kind = folder is Playlist ? "Playlist" : "Collection",
                        ContainerId = folder.Id,
                        OwnerUserId = folder is Playlist playlist ? playlist.OwnerUserId : Guid.Empty,
                        Index = i,
                        OldItemId = itemId,
                        Before = Enumerable.Range(1, Anchors)
                            .Where(n => i - n >= 0 && children[i - n].ItemId.HasValue)
                            .Select(n => children[i - n].ItemId!.Value)
                            .ToList(),
                        After = Enumerable.Range(1, Anchors)
                            .Where(n => i + n < children.Length && children[i + n].ItemId.HasValue)
                            .Select(n => children[i + n].ItemId!.Value)
                            .ToList()
                    };

                    if (!result.TryGetValue(itemId, out var list))
                    {
                        result[itemId] = list = new List<LinkEntry>();
                    }

                    list.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playlists und Sammlungen konnten nicht gelesen werden");
        }

        return result;
    }

    /// <summary>
    /// Die Verweise auf die Dateien (und ihre Hauptversion, falls es Versionen sind).
    /// </summary>
    /// <param name="paths">Die Dateien vor dem Umbenennen.</param>
    /// <param name="snapshot">Vorher gelesener Stand oder null, dann wird jetzt gelesen.</param>
    /// <returns>Die Verweise.</returns>
    public List<LinkEntry> Capture(IEnumerable<string> paths, Dictionary<Guid, List<LinkEntry>>? snapshot = null)
    {
        snapshot ??= Snapshot();
        var ids = new HashSet<Guid>();
        foreach (var path in paths)
        {
            var item = LinkKeeper.FindItem(_libraryManager, path);
            if (item is null)
            {
                continue;
            }

            ids.Add(item.Id);
            if (item is Video video && video.PrimaryVersionId is Guid primary)
            {
                ids.Add(primary);
            }
        }

        return ids.Where(snapshot.ContainsKey).SelectMany(id => snapshot[id]).ToList();
    }

    /// <summary>
    /// Sucht den Eintrag zu einer Datei. Anders als FindByPath findet das auch weitere Versionen eines Films
    /// oder einer Folge, die Jellyfin hinter der Hauptversion versteckt.
    /// </summary>
    /// <param name="libraryManager">Der LibraryManager.</param>
    /// <param name="path">Der Dateipfad.</param>
    /// <returns>Der Eintrag oder null.</returns>
    public static BaseItem? FindItem(ILibraryManager libraryManager, string path)
        => libraryManager.FindByPath(path, false)
            ?? libraryManager.GetItemList(new InternalItemsQuery { Path = path, IsFolder = false, IncludeOwnedItems = true })
                .OrderByDescending(i => i.DateCreated)
                .FirstOrDefault();

    /// <summary>
    /// Uebertraegt einen Verweis auf den neuen Eintrag. In Playlists kommt er an die alte Stelle: hinter den
    /// naechsten noch vorhandenen Vorgaenger (oder vor den naechsten Nachfolger). Ist der unmittelbare Vorgaenger
    /// selbst gerade umbenannt und noch nicht wieder eingesetzt, wartet der Verweis, damit die Reihenfolge stimmt.
    /// Bei Versionen kommt der Film selbst (die Hauptversion) hinein, wie bei Jellyfin ueblich.
    /// </summary>
    /// <param name="link">Der Verweis.</param>
    /// <param name="newItem">Der neue Eintrag.</param>
    /// <param name="pending">Alte Eintraege, deren Verweise noch auf ihre Uebertragung warten.</param>
    /// <param name="force">Nicht mehr warten, sondern bestmoeglich einsetzen.</param>
    /// <returns>True, wenn eingesetzt; false, wenn nichts zu tun war; null, wenn spaeter erneut versucht werden soll.</returns>
    public async Task<bool?> RestoreAsync(LinkEntry link, BaseItem newItem, ISet<Guid> pending, bool force)
    {
        var targetId = newItem is Video video && video.PrimaryVersionId is Guid primary ? primary : newItem.Id;
        try
        {
            if (link.Kind == "Playlist")
            {
                if (_libraryManager.GetItemById(link.ContainerId) is not Playlist playlist || !playlist.LinkedChildrenLoaded)
                {
                    link.Done = true;
                    return false;
                }

                var ids = playlist.LinkedChildren.Select(c => c.ItemId ?? Guid.Empty).ToList();
                var changed = false;
                if (!ids.Contains(targetId))
                {
                    var position = FindPosition(link, ids, pending, force);
                    if (position is null)
                    {
                        return null;
                    }

                    await _playlistManager.AddItemToPlaylistAsync(link.ContainerId, new[] { targetId }, position.Value, link.OwnerUserId).ConfigureAwait(false);
                    changed = true;
                }

                // Toten alten Eintrag entfernen, falls Jellyfin ihn noch fuehrt.
                if (!link.OldItemId.Equals(targetId) && ids.Contains(link.OldItemId))
                {
                    await _playlistManager.RemoveItemFromPlaylistAsync(link.ContainerId.ToString("N"), new[] { link.OldItemId.ToString("N") }).ConfigureAwait(false);
                }

                Remember(link.OldItemId, targetId);
                link.Done = true;
                return changed;
            }
            else
            {
                if (_libraryManager.GetItemById(link.ContainerId) is not BoxSet collection)
                {
                    link.Done = true;
                    return false;
                }

                var added = false;
                if (!collection.ContainsLinkedChildByItemId(targetId))
                {
                    await _collectionManager.AddToCollectionAsync(link.ContainerId, new[] { targetId }).ConfigureAwait(false);
                    added = true;
                }

                if (!link.OldItemId.Equals(targetId) && collection.LinkedChildren.Any(c => c.ItemId.Equals(link.OldItemId)))
                {
                    await _collectionManager.RemoveFromCollectionAsync(link.ContainerId, new[] { link.OldItemId }).ConfigureAwait(false);
                }

                Remember(link.OldItemId, targetId);
                link.Done = true;
                return added;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Kind} {Id}: Verweis auf {Item} konnte nicht übertragen werden", link.Kind, link.ContainerId, newItem.Path);
            link.Done = true;
            return false;
        }
    }

    /// <summary>
    /// Uebertraegt alle offenen Verweise eines umbenannten Eintrags.
    /// </summary>
    /// <param name="links">Die Verweise.</param>
    /// <param name="newItem">Der neue Eintrag.</param>
    /// <param name="pending">Alte Eintraege, deren Verweise noch warten.</param>
    /// <param name="force">Nicht mehr auf Vorgaenger warten.</param>
    /// <returns>Anzahl eingesetzter Verweise und ob noch welche warten.</returns>
    public async Task<(int Restored, bool Waiting)> RestoreAllAsync(IEnumerable<LinkEntry> links, BaseItem newItem, ISet<Guid> pending, bool force)
    {
        var restored = 0;
        var waiting = false;
        foreach (var link in links.Where(l => !l.Done))
        {
            var result = await RestoreAsync(link, newItem, pending, force).ConfigureAwait(false);
            if (result is null)
            {
                waiting = true;
            }
            else if (result.Value)
            {
                restored++;
            }
        }

        return (restored, waiting);
    }

    /// <summary>
    /// Alte Eintraege, deren Verweise noch nicht uebertragen sind.
    /// </summary>
    /// <param name="links">Alle gemerkten Verweise.</param>
    /// <returns>Die alten Ids.</returns>
    public static HashSet<Guid> PendingIds(IEnumerable<LinkEntry> links) => links.Where(l => !l.Done).Select(l => l.OldItemId).ToHashSet();

    private int? FindPosition(LinkEntry link, List<Guid> ids, ISet<Guid> pending, bool force)
    {
        // Steht der alte Verweis noch drin, genau dort.
        var old = ids.IndexOf(link.OldItemId);
        if (old >= 0)
        {
            return old;
        }

        int Locate(Guid id)
        {
            // Auch ueber mehrere Umbenennungen hinweg (etwa erst vereinheitlicht, dann umgewandelt).
            for (var step = 0; step < 5; step++)
            {
                var index = ids.IndexOf(id);
                if (index >= 0)
                {
                    return index;
                }

                lock (_replacedLock)
                {
                    if (!_replaced.TryGetValue(id, out var replacement) || replacement.NewId.Equals(id))
                    {
                        return -1;
                    }

                    id = replacement.NewId;
                }
            }

            return -1;
        }

        foreach (var before in link.Before)
        {
            var index = Locate(before);
            if (index >= 0)
            {
                return index + 1;
            }

            // Der Vorgaenger wird selbst gerade umbenannt: erst ihn einsetzen lassen.
            if (!force && !before.Equals(link.OldItemId) && pending.Contains(before))
            {
                return null;
            }
        }

        foreach (var after in link.After)
        {
            var index = Locate(after);
            if (index >= 0)
            {
                return index;
            }

            if (!force && !after.Equals(link.OldItemId) && pending.Contains(after))
            {
                return null;
            }
        }

        return Math.Min(link.Index, ids.Count);
    }

    private void Remember(Guid oldId, Guid newId)
    {
        if (oldId.Equals(newId))
        {
            return;
        }

        lock (_replacedLock)
        {
            _replaced[oldId] = new Replacement { NewId = newId, When = DateTime.UtcNow };
            try
            {
                // Nur die letzten Tage aufheben.
                foreach (var old in _replaced.Where(r => r.Value.When < DateTime.UtcNow.AddDays(-7)).Select(r => r.Key).ToList())
                {
                    _replaced.Remove(old);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_replacedFile)!);
                var temp = _replacedFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_replaced.OrderByDescending(r => r.Value.When).Take(5000).ToDictionary(r => r.Key, r => r.Value)));
                File.Move(temp, _replacedFile, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ersetzte Einträge konnten nicht gespeichert werden");
            }
        }
    }

    private void LoadReplaced()
    {
        try
        {
            if (File.Exists(_replacedFile)
                && JsonSerializer.Deserialize<Dictionary<Guid, Replacement>>(File.ReadAllText(_replacedFile)) is { } stored)
            {
                foreach (var (oldId, replacement) in stored)
                {
                    _replaced[oldId] = replacement;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ersetzte Einträge konnten nicht gelesen werden");
        }
    }

    private sealed class Replacement
    {
        public Guid NewId { get; set; }

        public DateTime When { get; set; }
    }
}
