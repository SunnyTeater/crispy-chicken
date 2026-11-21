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

    // Codec-Angaben, wie sie in Dateinamen stehen: x264, h.265, HEVC, AVC, XviD ... - nur als eigenes Wort.
    private static readonly Regex _codecTag = new(
        @"(?<![A-Za-z0-9])(x\.?26[45]|h\.?26[45]|avc|hevc|av1|xvid|divx|mpeg-?[24]|vc-?1|vp9)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    /// Schaetzt die Groesse der neuen Datei. Grundlage ist das Verhaeltnis aus neuer und alter Videospur,
    /// gemessen an echten Umwandlungen (siehe <see cref="SizeModel"/>): es haengt davon ab, wie stark die Quelle
    /// schon komprimiert ist und welcher Qualitaetswert fuer das Ziel gewaehlt wird. Wie stark die Quelle
    /// komprimiert ist, sagt am besten ihr eigener CRF-Wert, wenn er in der Datei steht; sonst wird er aus
    /// Bitrate, Aufloesung und Bildrate abgeleitet. Ton, Untertitel und Anhaenge bleiben unveraendert.
    /// Die neue Datei kann groesser werden als die alte: das wird nicht gekappt.
    /// </summary>
    /// <param name="entry">Die Quelldatei.</param>
    /// <param name="target">Der Zielcodec.</param>
    /// <param name="encoder">Der Encoder.</param>
    /// <param name="quality">Der Qualitaetswert.</param>
    /// <param name="info">Die ausgelesenen Encoder-Angaben der Quelle, falls vorhanden.</param>
    /// <returns>Geschaetzte Groesse in Bytes.</returns>
    public static long EstimateSize(MediaEntry entry, TargetCodec target, EncoderInfo encoder, int quality, SourceInfo? info = null)
    {
        if (entry.Size <= 0 || entry.DurationSeconds <= 0)
        {
            return entry.Size;
        }

        var totalBitrate = entry.Size * 8d / entry.DurationSeconds;

        // Ohne bekannte Videobitrate: ueblicherweise macht die Videospur den Grossteil der Datei aus.
        var sourceVideo = entry.VideoBitrate > 0 ? Math.Min(entry.VideoBitrate, totalBitrate) : totalBitrate * 0.9;
        var other = Math.Max(0, totalBitrate - sourceVideo);

        var byCrf = UsesSourceCrf(entry, info);
        var sourceLevel = byCrf ? info!.Crf!.Value : ImpliedSourceLevel(entry, sourceVideo);
        var model = SizeModel.For(target.Codec, byCrf);
        var ratio = Math.Pow(2, model.Offset + (model.SourceSlope * sourceLevel) - (model.TargetSlope * quality));
        if (encoder.Hardware)
        {
            ratio *= HardwarePenalty;
        }

        // Ausreisser des Modells begrenzen, aber Wachstum ausdruecklich zulassen.
        ratio = Math.Clamp(ratio, 0.03, 4.0);
        return (long)(((sourceVideo * ratio) + other) * entry.DurationSeconds / 8d);
    }

    /// <summary>
    /// Ob der CRF-Wert der Quelle in die Schaetzung eingeht. Das Modell ist an x264-Quellen gemessen;
    /// bei anderen Quellen rechnet es mit der Bitrate.
    /// </summary>
    /// <param name="entry">Die Quelldatei.</param>
    /// <param name="info">Die ausgelesenen Encoder-Angaben.</param>
    /// <returns>True, wenn der CRF-Wert benutzt wird.</returns>
    public static bool UsesSourceCrf(MediaEntry entry, SourceInfo? info)
        => info?.Crf is not null && info.Encoder == "x264" && entry.Codec == "h264";

    /// <summary>
    /// Leitet aus der Bitrate ab, welchem x264-Qualitaetswert die Quelle ungefaehr entspricht: eine hohe Bitrate
    /// fuer Aufloesung und Bildrate bedeutet schwach komprimiert (kleiner Wert), eine niedrige stark komprimiert.
    /// </summary>
    private static double ImpliedSourceLevel(MediaEntry entry, double sourceVideo)
    {
        var pixels = entry.Width > 0 && entry.Height > 0 ? entry.Width * (double)entry.Height : ReferencePixels;
        var fps = entry.Fps is > 1 and < 300 ? entry.Fps : ReferenceFps;
        var typical = SizeModel.X264ReferenceBitrate * Math.Pow(pixels / ReferencePixels, 0.75) * Math.Pow(fps / ReferenceFps, 0.5);

        // Andere Codecs brauchen fuer dieselbe Qualitaet mehr oder weniger Bitrate als H.264.
        var asH264 = sourceVideo / Efficiency(entry.Codec);
        var level = SizeModel.X264ReferenceLevel - (SizeModel.X264StepsPerHalving * Math.Log2(Math.Max(asH264, 1) / typical));
        return Math.Clamp(level, 5, 45);
    }

    /// <summary>
    /// Bildet den Dateinamen (ohne Endung) der neuen Datei. Steht der alte Codec im Namen, wird er ersetzt
    /// ("Film x264" wird "Film x265"); sonst wird der neue Codec als Versionsname angehaengt ("Film - HEVC").
    /// Der Name unterscheidet sich immer vom Original, damit beide Dateien nebeneinander liegen koennen.
    /// </summary>
    /// <param name="baseName">Dateiname des Originals ohne Endung.</param>
    /// <param name="targetCodec">Der Zielcodec.</param>
    /// <returns>Der neue Dateiname ohne Endung.</returns>
    public static string BuildTargetName(string baseName, string targetCodec)
    {
        var label = targetCodec switch
        {
            "hevc" => "HEVC",
            "av1" => "AV1",
            _ => "H264"
        };

        var replaced = _codecTag.Replace(baseName, match => ReplaceTag(match.Value, targetCodec, label));
        return string.Equals(replaced, baseName, StringComparison.Ordinal) ? baseName + "-" + label : replaced;
    }

    /// <summary>
    /// Kurzname eines Codecs fuer Dateinamen und die Versionsauswahl in Jellyfin ("HEVC", "H264", ...).
    /// Nur Buchstaben und Ziffern, damit Jellyfin den Namen weder als Extra noch als Teil eines Mehrteilers liest.
    /// </summary>
    /// <param name="codec">Der Codec, wie ffprobe ihn nennt.</param>
    /// <returns>Der Kurzname.</returns>
    public static string CodecLabel(string codec)
    {
        var label = codec switch
        {
            "hevc" => "HEVC",
            "av1" => "AV1",
            "h264" => "H264",
            "mpeg2video" => "MPEG2",
            "mpeg1video" => "MPEG1",
            "mpeg4" or "msmpeg4v3" or "msmpeg4v2" => "MPEG4",
            "vc1" => "VC1",
            "wmv3" or "wmv2" or "wmv1" => "WMV",
            "vp9" => "VP9",
            "vp8" => "VP8",
            _ => new string((codec ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant()
        };
        return label.Length == 0 ? "VIDEO" : label;
    }

    private static string ReplaceTag(string tag, string targetCodec, string label)
    {
        var first = char.ToLowerInvariant(tag[0]);
        var numbered = (first == 'x' || first == 'h') && (tag.EndsWith("264", StringComparison.Ordinal) || tag.EndsWith("265", StringComparison.Ordinal));
        if (!numbered || targetCodec == "av1")
        {
            return label;
        }

        // Schreibweise des Originals behalten: x264 -> x265, H.264 -> H.265.
        return tag[0] + (tag.Contains('.', StringComparison.Ordinal) ? "." : string.Empty) + (targetCodec == "hevc" ? "265" : "264");
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
    /// <param name="gpuDecode">Ob (bei NVENC) auch das Dekodieren der Quelle auf der GPU laufen soll.</param>
    /// <returns>Die Argumentliste.</returns>
    public static List<string> BuildArguments(Job job, string format, int threads, string vaapiDevice, bool gpuDecode)
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

        if (gpuDecode && UsesGpuDecode(job.Encoder))
        {
            // Ohne diese Optionen dekodiert ffmpeg die Quelle auf der CPU und nur das Kodieren laeuft auf der GPU:
            // die CPU ist dann trotz NVENC voll ausgelastet. So bleiben die Bilder durchgehend im Grafikspeicher.
            args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
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
            var count = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (job.Encoder == "libx265")
            {
                // x265 nimmt ueber "-threads" hoechstens 16 an und bricht bei mehr ab ("frameNumThreads must be ...").
                // Die Groesse des Thread-Pools begrenzt die Auslastung ohne diese Obergrenze.
                args.AddRange(new[] { "-x265-params", "pools=" + count });
            }
            else
            {
                args.AddRange(new[] { "-threads", count });
            }
        }

        args.AddRange(new[] { "-max_muxing_queue_size", "4096", "-progress", "pipe:1", "-f", format, "file:" + job.TempPath });
        return args;
    }

    /// <summary>
    /// Ob fuer einen Encoder zusaetzlich das Dekodieren auf der GPU versucht wird.
    /// </summary>
    /// <param name="encoder">Name des Encoders.</param>
    /// <returns>True bei den NVENC-Encodern.</returns>
    public static bool UsesGpuDecode(string encoder) => encoder.EndsWith("_nvenc", StringComparison.Ordinal);

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

/// <summary>
/// Messwerte fuer die Groessenschaetzung: log2(neue Videospur / alte Videospur) = Offset + SourceSlope * Quellwert
/// - TargetSlope * gewaehlter Qualitaetswert. Der Quellwert ist der CRF-Wert der Quelle, wenn er in der Datei steht,
/// sonst der aus der Bitrate abgeleitete Wert; fuer beide Faelle gibt es eigene Messwerte.
/// Angepasst an 90 Umwandlungen von neun echten H.264-Ausschnitten (x264, CRF 18 bis 28, 404p bis 1080p) mit dem
/// ffmpeg von Jellyfin. Mittlere Abweichung: etwa Faktor 1,3 mit CRF-Wert, etwa Faktor 1,6 nur mit Bitrate.
/// </summary>
/// <param name="Offset">Grundwert.</param>
/// <param name="SourceSlope">Einfluss der Quelle: je staerker sie schon komprimiert ist, desto weniger laesst sich sparen.</param>
/// <param name="TargetSlope">Einfluss des gewaehlten Qualitaetswerts.</param>
public sealed record SizeModel(double Offset, double SourceSlope, double TargetSlope)
{
    /// <summary>Bezugs-Bitrate von x264 bei 1080p und 24 Bildern pro Sekunde fuer den abgeleiteten Quellwert.</summary>
    public const double X264ReferenceBitrate = 4_000_000;

    /// <summary>Bezugswert (CRF) zu <see cref="X264ReferenceBitrate"/>.</summary>
    public const double X264ReferenceLevel = 23;

    /// <summary>So viele Stufen des abgeleiteten Quellwerts entsprechen einer Halbierung der Bitrate.</summary>
    public const double X264StepsPerHalving = 6;

    private static readonly SizeModel _hevcCrf = new(-0.150, 0.180, 0.217);
    private static readonly SizeModel _hevcBitrate = new(0.803, 0.114, 0.218);
    private static readonly SizeModel _av1Crf = new(-2.151, 0.190, 0.108);
    private static readonly SizeModel _av1Bitrate = new(-1.438, 0.134, 0.107);
    private static readonly SizeModel _h264Crf = new(0.618, 0.141, 0.197);
    private static readonly SizeModel _h264Bitrate = new(1.488, 0.089, 0.209);

    /// <summary>
    /// Liefert die Messwerte fuer einen Zielcodec.
    /// </summary>
    /// <param name="codec">Der Zielcodec.</param>
    /// <param name="sourceCrfKnown">Ob der CRF-Wert der Quelle bekannt ist.</param>
    /// <returns>Die Messwerte.</returns>
    public static SizeModel For(string codec, bool sourceCrfKnown) => codec switch
    {
        "hevc" => sourceCrfKnown ? _hevcCrf : _hevcBitrate,
        "av1" => sourceCrfKnown ? _av1Crf : _av1Bitrate,
        _ => sourceCrfKnown ? _h264Crf : _h264Bitrate
    };
}
