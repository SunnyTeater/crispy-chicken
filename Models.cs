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
    Cancelled
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

    public string? Message { get; set; }

    public DateTime Created { get; set; }

    public DateTime? Started { get; set; }

    public DateTime? Finished { get; set; }
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
