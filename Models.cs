using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Eine Videodatei der Bibliothek (Film oder Folge).
/// </summary>
public sealed class MediaEntry
{
    public Guid Id { get; set; }

    /// <summary>Gets or sets "Movie" oder "Episode".</summary>
    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string Codec { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public double Fps { get; set; }

    /// <summary>Gets or sets die Bitrate der Videospur in Bit/s (0 = unbekannt).</summary>
    public long VideoBitrate { get; set; }

    public long Size { get; set; }

    public double DurationSeconds { get; set; }

    public string Container { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets den Farbraum, z. B. SDR, HDR10, DOVI.</summary>
    public string VideoRange { get; set; } = "SDR";

    /// <summary>Gets or sets den Grund, falls die Datei nicht umgewandelt werden kann.</summary>
    public string? Blocked { get; set; }

    /// <summary>Gets or sets a value indicating whether die Encoder-Angaben der Datei schon ausgelesen wurden.</summary>
    public bool SourceRead { get; set; }

    /// <summary>Gets or sets den Encoder der Quelle ("x264"/"x265"), soweit die Datei ihn nennt.</summary>
    public string? SourceEncoder { get; set; }

    /// <summary>Gets or sets den CRF-Wert, mit dem die Quelle erzeugt wurde.</summary>
    public double? SourceCrf { get; set; }

    /// <summary>Gets or sets die Zielbitrate (kbit/s), mit der die Quelle erzeugt wurde.</summary>
    public int? SourceKbps { get; set; }

    /// <summary>Gets or sets den festen Quantisierer, mit dem die Quelle erzeugt wurde.</summary>
    public int? SourceQp { get; set; }

    /// <summary>Gets or sets a value indicating whether bereits ein offener Job existiert.</summary>
    public bool Queued { get; set; }
}

/// <summary>
/// Zustand eines Jobs.
/// </summary>
public enum JobState
{
    Queued,
    Running,
    Paused,
    Finalizing,
    Done,
    Failed,
    Cancelled,

    /// <summary>Neue Datei liegt neben dem Original, das Original wartet auf die Freigabe zum Loeschen.</summary>
    AwaitingApproval,

    /// <summary>Neue Datei verworfen, Original behalten.</summary>
    Rejected
}

/// <summary>
/// Eine Umbenennung von Original und/oder neuer Datei. Wird vor dem ersten Schritt gespeichert, damit sie
/// nach einem Absturz zu Ende gefuehrt werden kann.
/// </summary>
public sealed class PendingRename
{
    /// <summary>Gets or sets den bisherigen Pfad des Originals (null: Original bleibt).</summary>
    public string? OldSource { get; set; }

    /// <summary>Gets or sets den neuen Pfad des Originals.</summary>
    public string? NewSource { get; set; }

    /// <summary>Gets or sets den bisherigen Pfad der neuen Datei (null: bleibt).</summary>
    public string? OldTarget { get; set; }

    /// <summary>Gets or sets den neuen Pfad der neuen Datei.</summary>
    public string? NewTarget { get; set; }
}

/// <summary>
/// Eine Begleitdatei (externer Untertitel), die fuer die neue Videodatei kopiert wurde.
/// </summary>
public sealed class SidecarCopy
{
    public string Original { get; set; } = string.Empty;

    public string Copy { get; set; } = string.Empty;
}

/// <summary>
/// Ein Umwandlungsauftrag fuer genau eine Datei.
/// </summary>
public sealed class Job
{
    public string Id { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string TempPath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? BackupFile { get; set; }

    public string SourceCodec { get; set; } = string.Empty;

    public string TargetCodec { get; set; } = string.Empty;

    public string Encoder { get; set; } = string.Empty;

    public int Quality { get; set; }

    public string Speed { get; set; } = "medium";

    public long SourceSize { get; set; }

    public long EstimatedSize { get; set; }

    public long? ResultSize { get; set; }

    public double DurationSeconds { get; set; }

    public JobState State { get; set; }

    /// <summary>Gets or sets den Fortschritt von 0 bis 1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets die Geschwindigkeit relativ zur Echtzeit.</summary>
    public double EncodeSpeed { get; set; }

    /// <summary>Gets or sets die reine Rechenzeit in Sekunden (ohne Pausen wegen aktiver Streams).</summary>
    public double ActiveSeconds { get; set; }

    /// <summary>Gets or sets die mittlere Geschwindigkeit des fertigen Jobs relativ zur Echtzeit. Grundlage fuer die Zeitschaetzung weiterer Jobs.</summary>
    public double AverageSpeed { get; set; }

    /// <summary>Gets or sets die Zahl der Fehlermeldungen, die ffmpeg beim Lesen der Quelle ausgegeben hat, obwohl die Umwandlung durchlief.</summary>
    public int SourceErrors { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double Fps { get; set; }

    public string? Message { get; set; }

    public DateTime Created { get; set; }

    public DateTime? Started { get; set; }

    public DateTime? Finished { get; set; }

    /// <summary>Gets or sets den Zeitpunkt, zu dem die neue Datei fertig war. Ab hier laeuft die Frist der automatischen Freigabe.</summary>
    public DateTime? Converted { get; set; }

    public List<SidecarCopy> Sidecars { get; set; } = new();

    /// <summary>Gets or sets den Gesehen-Stand der Benutzer, der nach der Freigabe noch auf die neue Datei zu uebertragen ist.</summary>
    public List<UserState> PendingUserStates { get; set; } = new();

    /// <summary>Gets or sets die Schaetzung des Rechenmodells ohne den Abgleich mit frueheren Ergebnissen (0 bei alten Jobs).</summary>
    public long ModelSize { get; set; }

    /// <summary>Gets or sets den CRF-Wert der Quelle, soweit bekannt.</summary>
    public double? SourceCrf { get; set; }

    /// <summary>Gets or sets die Art des Eintrags ("Movie" oder "Episode"); null bei Jobs aus Versionen vor 1.3.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets a value indicating whether neue Datei und Original nach Jellyfins Versionsregel benannt sind.</summary>
    public bool NamingApplied { get; set; }

    /// <summary>Gets or sets den Stand der Benennungsregel, nach der zuletzt benannt wurde (0 = vor 1.4).</summary>
    public int NamingVersion { get; set; }

    /// <summary>Gets or sets den Pfad des Originals, bevor das Plugin es umbenannt hat; null, wenn es nicht umbenannt wurde.</summary>
    public string? OriginalPath { get; set; }

    /// <summary>Gets or sets eine begonnene Umbenennung, die nach einem Absturz zu Ende gefuehrt wird.</summary>
    public PendingRename? PendingRename { get; set; }

    /// <summary>Gets or sets den Gesehen-Status des Originals vor dem Umbenennen, der auf den Film uebertragen wird.</summary>
    public List<UserState> RenameUserStates { get; set; } = new();

    /// <summary>Gets or sets die Verweise aus Playlists und Sammlungen auf das Original vor dem Umbenennen.</summary>
    public List<LinkEntry> RenameLinks { get; set; } = new();

    /// <summary>Gets or sets die Verweise aus Playlists und Sammlungen, die nach Freigabe/Verwerfen uebertragen werden.</summary>
    public List<LinkEntry> PendingLinks { get; set; } = new();

    /// <summary>Gets or sets den Zeitpunkt des Umbenennens.</summary>
    public DateTime? RenamedAt { get; set; }

    /// <summary>
    /// Kopiert den Job. Die Listen werden neu angelegt, ihre Eintraege aendern sich nach dem Anlegen nicht mehr.
    /// </summary>
    /// <returns>Die Kopie.</returns>
    public Job Copy()
    {
        var copy = (Job)MemberwiseClone();
        copy.Sidecars = new List<SidecarCopy>(Sidecars);
        copy.PendingUserStates = new List<UserState>(PendingUserStates);
        copy.RenameUserStates = new List<UserState>(RenameUserStates);
        copy.RenameLinks = new List<LinkEntry>(RenameLinks);
        copy.PendingLinks = new List<LinkEntry>(PendingLinks);
        return copy;
    }
}

/// <summary>
/// Gesehen-Stand eines Benutzers fuer eine Datei.
/// </summary>
public sealed class UserState
{
    public Guid UserId { get; set; }

    public bool Played { get; set; }

    public bool IsFavorite { get; set; }

    public long PlaybackPositionTicks { get; set; }

    public int PlayCount { get; set; }

    public DateTime? LastPlayedDate { get; set; }
}

/// <summary>
/// Anfrage fuer Schaetzung und Planung.
/// </summary>
public sealed class ConvertRequest
{
    public List<Guid> ItemIds { get; set; } = new();

    public string TargetCodec { get; set; } = string.Empty;

    public string Encoder { get; set; } = string.Empty;

    public int Quality { get; set; }

    public string Speed { get; set; } = "medium";
}
