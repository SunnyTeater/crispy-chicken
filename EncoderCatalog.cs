using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Ein ffmpeg-Encoder fuer einen Zielcodec.
/// </summary>
/// <param name="Name">Name des Encoders in ffmpeg.</param>
/// <param name="Label">Anzeigename.</param>
/// <param name="Hardware">Ob der Encoder eine GPU braucht.</param>
public sealed record EncoderInfo(string Name, string Label, bool Hardware);

/// <summary>
/// Eine Verlust-Stufe.
/// </summary>
/// <param name="Label">Anzeigename.</param>
/// <param name="Value">Qualitaetswert (CRF bzw. Entsprechung, kleiner = weniger Verlust).</param>
public sealed record QualityPreset(string Label, int Value);

/// <summary>
/// Ein Zielcodec mit seinen Encodern und Qualitaetsstufen.
/// </summary>
public sealed class TargetCodec
{
    public string Codec { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<EncoderInfo> Encoders { get; init; } = Array.Empty<EncoderInfo>();

    public IReadOnlyList<QualityPreset> Presets { get; init; } = Array.Empty<QualityPreset>();

    public int MinQuality { get; init; }

    public int MaxQuality { get; init; }

    /// <summary>Gets die typische Bitrate in Bit/s fuer 1080p bei 24 fps und <see cref="ReferenceQuality"/>.</summary>
    public double ReferenceBitrate { get; init; }

    public int ReferenceQuality { get; init; }

    /// <summary>Gets den Qualitaetswert, bei dem das Ergebnis vom Original praktisch nicht zu unterscheiden ist.</summary>
    public int TransparentQuality { get; init; }
}

/// <summary>
/// Kennt die Zielcodecs, baut die ffmpeg-Argumente und schaetzt Dateigroessen.
/// </summary>
public static class EncoderCatalog
{
    /// <summary>
    /// Die waehlbaren Geschwindigkeiten.
    /// </summary>
    public static readonly string[] Speeds = { "slow", "medium", "fast" };

    private const double ReferencePixels = 1920d * 1080d;
    private const double ReferenceFps = 24d;

    // Hardware-Encoder brauchen fuer dieselbe Qualitaet spuerbar mehr Bitrate als die CPU-Encoder.
    private const double HardwarePenalty = 1.35;

    private static readonly Regex _vaapiDevice = new("^/dev/dri/renderD[0-9]{1,4}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Die Zielcodecs.
    /// </summary>
    public static readonly IReadOnlyList<TargetCodec> Targets = new[]
    {
        new TargetCodec
        {
            Codec = "hevc",
            Label = "H.265 / HEVC",
            Encoders = new[]
            {
                new EncoderInfo("libx265", "CPU (x265)", false),
                new EncoderInfo("hevc_nvenc", "NVIDIA NVENC", true),
                new EncoderInfo("hevc_qsv", "Intel Quick Sync", true),
                new EncoderInfo("hevc_vaapi", "VAAPI", true)
            },
            Presets = new[]
            {
                new QualityPreset("Sehr gering (kaum sichtbar)", 18),
                new QualityPreset("Gering", 22),
                new QualityPreset("Mittel", 26),
                new QualityPreset("Hoch (kleine Datei)", 30)
            },
            MinQuality = 0,
            MaxQuality = 51,
            ReferenceBitrate = 2_200_000,
            ReferenceQuality = 23,
            TransparentQuality = 20
        },
        new TargetCodec
        {
            Codec = "av1",
            Label = "AV1",
            Encoders = new[]
            {
                new EncoderInfo("libsvtav1", "CPU (SVT-AV1)", false),
                new EncoderInfo("av1_nvenc", "NVIDIA NVENC", true),
                new EncoderInfo("av1_qsv", "Intel Quick Sync", true),
                new EncoderInfo("av1_vaapi", "VAAPI", true)
            },
            Presets = new[]
            {
                new QualityPreset("Sehr gering (kaum sichtbar)", 22),
                new QualityPreset("Gering", 28),
                new QualityPreset("Mittel", 34),
                new QualityPreset("Hoch (kleine Datei)", 40)
            },
            MinQuality = 0,
            MaxQuality = 63,
            ReferenceBitrate = 1_800_000,
            ReferenceQuality = 30,
            TransparentQuality = 26
        },
        new TargetCodec
        {
            Codec = "h264",
            Label = "H.264 / AVC",
            Encoders = new[]
            {
                new EncoderInfo("libx264", "CPU (x264)", false),
                new EncoderInfo("h264_nvenc", "NVIDIA NVENC", true),
                new EncoderInfo("h264_qsv", "Intel Quick Sync", true),
                new EncoderInfo("h264_vaapi", "VAAPI", true)
            },
            Presets = new[]
            {
                new QualityPreset("Sehr gering (kaum sichtbar)", 17),
                new QualityPreset("Gering", 20),
                new QualityPreset("Mittel", 23),
                new QualityPreset("Hoch (kleine Datei)", 27)
            },
            MinQuality = 0,
            MaxQuality = 51,
            ReferenceBitrate = 4_000_000,
            ReferenceQuality = 23,
            TransparentQuality = 19
        }
    };

    /// <summary>
    /// Prueft eine Anfrage gegen den Katalog.
    /// </summary>
    /// <param name="request">Die Anfrage.</param>
    /// <param name="target">Der Zielcodec.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <returns>Fehlertext oder null.</returns>
    public static string? Validate(ConvertRequest request, out TargetCodec? target, out EncoderInfo? encoder)
    {
        encoder = null;
        target = Targets.FirstOrDefault(t => string.Equals(t.Codec, request.TargetCodec, StringComparison.Ordinal));
        if (target is null)
        {
            return "Unbekannter Zielcodec.";
        }

        encoder = target.Encoders.FirstOrDefault(e => string.Equals(e.Name, request.Encoder, StringComparison.Ordinal));
        if (encoder is null)
        {
            return "Der Encoder passt nicht zum Zielcodec.";
        }

        if (request.Quality < target.MinQuality || request.Quality > target.MaxQuality)
        {
            return "Der Qualitätswert muss zwischen " + target.MinQuality + " und " + target.MaxQuality + " liegen.";
        }

        if (!Speeds.Contains(request.Speed, StringComparer.Ordinal))
        {
            return "Unbekannte Geschwindigkeit.";
        }

        return null;
    }

    /// <summary>
    /// Schaetzt grob die Dateigroesse nach der Umwandlung.
    /// </summary>
    /// <param name="entry">Die Quelldatei.</param>
    /// <param name="target">Der Zielcodec.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <param name="quality">Der Qualitaetswert.</param>
    /// <returns>Geschaetzte Groesse in Bytes.</returns>
    public static long EstimateSize(MediaEntry entry, TargetCodec target, EncoderInfo encoder, int quality)
    {
        if (entry.Size <= 0 || entry.DurationSeconds <= 0)
        {
            return entry.Size;
        }

        var totalBitrate = entry.Size * 8d / entry.DurationSeconds;

        // Ohne bekannte Videobitrate: ueblicherweise macht die Videospur den Grossteil der Datei aus.
        var sourceVideo = entry.VideoBitrate > 0 ? Math.Min(entry.VideoBitrate, totalBitrate) : totalBitrate * 0.9;
        var other = Math.Max(0, totalBitrate - sourceVideo);

        var pixels = entry.Width > 0 && entry.Height > 0 ? entry.Width * (double)entry.Height : ReferencePixels;
        var fps = entry.Fps is > 1 and < 300 ? entry.Fps : ReferenceFps;

        // Modell 1, aus Aufloesung und Bildrate: typische Bitrate des Zielcodecs.
        // Faustregel der CRF-Encoder: 6 Stufen halbieren bzw. verdoppeln die Bitrate.
        var typical = target.ReferenceBitrate
            * Math.Pow(2, (target.ReferenceQuality - quality) / 6d)
            * Math.Pow(pixels / ReferencePixels, 0.75)
            * Math.Pow(fps / ReferenceFps, 0.5);

        // Modell 2, aus der Quelle: eine hohe Quellbitrate deutet auf schwer komprimierbares Bild (Korn, Bewegung).
        var relative = sourceVideo
            * (Efficiency(target.Codec) / Efficiency(entry.Codec))
            * Math.Pow(2, (target.TransparentQuality - quality) / 6d);

        // Beide Modelle liegen einzeln oft daneben, ihr geometrisches Mittel trifft besser.
        var video = Math.Sqrt(typical * relative);

        if (encoder.Hardware)
        {
            video *= HardwarePenalty;
        }

        // Aus einer bereits komprimierten Quelle wird die neue Videospur praktisch nie groesser als die alte.
        video = Math.Min(video, sourceVideo * 0.95);

        return (long)((video + other) * entry.DurationSeconds / 8d);
    }

    /// <summary>
    /// Bitratenbedarf eines Codecs fuer gleiche Qualitaet, relativ zu H.264.
    /// </summary>
    private static double Efficiency(string codec) => codec switch
    {
        "mpeg2video" or "mpeg1video" => 2.0,
        "mpeg4" or "msmpeg4v3" or "msmpeg4v2" or "h263" => 1.4,
        "vc1" or "wmv3" or "vp8" => 1.2,
        "vp9" => 0.7,
        "hevc" => 0.6,
        "av1" => 0.5,
        _ => 1.0
    };

    /// <summary>
    /// Bestimmt Dateiendung und ffmpeg-Format der Zieldatei. Der Container bleibt, wo der Zielcodec es erlaubt.
    /// </summary>
    /// <param name="sourcePath">Die Quelldatei.</param>
    /// <param name="targetCodec">Der Zielcodec.</param>
    /// <returns>Dateiendung (mit Punkt) und ffmpeg-Formatname.</returns>
    public static (string Extension, string Format) GetContainer(string sourcePath, string targetCodec)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        switch (extension)
        {
            case ".mkv":
                return (extension, "matroska");
            case ".mp4":
            case ".m4v":
                return (extension, "mp4");
            case ".mov":
                return targetCodec == "av1" ? (".mkv", "matroska") : (extension, "mov");
            case ".ts":
            case ".m2ts":
            case ".mts":
                return targetCodec == "av1" ? (".mkv", "matroska") : (extension, "mpegts");
            default:
                // avi, wmv, mpg, flv, webm ...: diese Container koennen die Zielcodecs bzw. die
                // kopierten Tonspuren nicht zuverlaessig aufnehmen.
                return (".mkv", "matroska");
        }
    }

    /// <summary>
    /// Baut die ffmpeg-Argumente. Jedes Argument ist ein eigenes Listenelement, es gibt keine Shell.
    /// </summary>
    /// <param name="job">Der Job.</param>
    /// <param name="format">Das ffmpeg-Format der Zieldatei.</param>
    /// <param name="threads">Threads (0 = automatisch).</param>
    /// <param name="vaapiDevice">Das VAAPI-Geraet.</param>
    /// <returns>Die Argumentliste.</returns>
    public static List<string> BuildArguments(Job job, string format, int threads, string vaapiDevice)
    {
        // Kein "-y": ffmpeg soll eine vorhandene Datei nie ueberschreiben.
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-nostats" };
        var isVaapi = job.Encoder.EndsWith("_vaapi", StringComparison.Ordinal);

        if (isVaapi)
        {
            if (!_vaapiDevice.IsMatch(vaapiDevice))
            {
                throw new InvalidOperationException("Das VAAPI-Gerät muss die Form /dev/dri/renderD128 haben.");
            }

            args.AddRange(new[] { "-vaapi_device", vaapiDevice });
        }

        // Die Quelle darf nur lokale Dateien lesen: eine praeparierte Mediendatei (etwa eine als Video
        // getarnte Wiedergabeliste) kann ffmpeg so nicht zu Netzwerkzugriffen bewegen.
        // "file:" verhindert, dass ffmpeg einen Dateinamen als Protokoll oder Option deutet.
        args.AddRange(new[] { "-protocol_whitelist", "file", "-i", "file:" + job.SourcePath });

        // Hauptvideospuren (V = ohne eingebettete Cover), alle Ton- und Untertitelspuren.
        args.AddRange(new[] { "-map", "0:V", "-map", "0:a?", "-map", "0:s?" });
        if (format == "matroska")
        {
            // Anhaenge (Schriftarten, Cover) gibt es nur in Matroska.
            args.AddRange(new[] { "-map", "0:t?" });
        }

        args.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0", "-c", "copy" });

        if (isVaapi)
        {
            args.AddRange(new[] { "-vf", "format=nv12,hwupload" });
        }

        args.AddRange(new[] { "-c:V", job.Encoder });
        args.AddRange(QualityArguments(job));

        if (job.TargetCodec == "hevc" && (format == "mp4" || format == "mov"))
        {
            // Ohne dieses Kennzeichen spielen Apple-Geraete HEVC in MP4 nicht ab.
            args.AddRange(new[] { "-tag:v", "hvc1" });
        }

        if (format == "mp4" || format == "mov")
        {
            args.AddRange(new[] { "-movflags", "+faststart" });
        }

        if (threads > 0)
        {
            args.AddRange(new[] { "-threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        args.AddRange(new[] { "-max_muxing_queue_size", "4096", "-progress", "pipe:1", "-f", format, "file:" + job.TempPath });
        return args;
    }

    private static IEnumerable<string> QualityArguments(Job job)
    {
        var q = job.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var speedIndex = Math.Max(0, Array.IndexOf(Speeds, job.Speed));

        switch (job.Encoder)
        {
            case "libx265":
            case "libx264":
                return new[] { "-crf", q, "-preset", job.Speed };
            case "libsvtav1":
                return new[] { "-crf", q, "-preset", new[] { "5", "8", "10" }[speedIndex] };
            case "hevc_nvenc":
            case "h264_nvenc":
            case "av1_nvenc":
                return new[] { "-rc", "vbr", "-cq", q, "-b:v", "0", "-preset", new[] { "p7", "p5", "p3" }[speedIndex] };
            case "hevc_qsv":
            case "h264_qsv":
            case "av1_qsv":
                return new[] { "-global_quality", q, "-preset", new[] { "slow", "medium", "fast" }[speedIndex] };
            case "hevc_vaapi":
            case "h264_vaapi":
            case "av1_vaapi":
                return new[] { "-rc_mode", "CQP", "-qp", q };
            default:
                throw new InvalidOperationException("Unbekannter Encoder.");
        }
    }
}
