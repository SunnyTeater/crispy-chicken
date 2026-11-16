using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Was der Encoder der Quelldatei ueber seine Einstellungen in die Videospur geschrieben hat.
/// </summary>
public sealed class SourceInfo
{
    /// <summary>Gets or sets den Encoder ("x264" oder "x265"), null wenn die Datei keine Angabe enthaelt.</summary>
    public string? Encoder { get; set; }

    /// <summary>Gets or sets den Qualitaetswert (CRF), mit dem die Datei erzeugt wurde.</summary>
    public double? Crf { get; set; }

    /// <summary>Gets or sets die Zielbitrate in kbit/s, wenn die Datei auf eine Bitrate statt auf Qualitaet kodiert wurde.</summary>
    public int? TargetKbps { get; set; }

    /// <summary>Gets or sets den festen Quantisierer (QP), wenn die Datei damit kodiert wurde.</summary>
    public int? Qp { get; set; }
}

/// <summary>
/// Liest aus H.264- und HEVC-Dateien die Einstellungen aus, die x264 und x265 in die Videospur schreiben
/// (unter anderem den CRF-Wert), und merkt sie sich. Gelesen wird im Hintergrund mit dem ffmpeg von Jellyfin:
/// nur die ersten Bilder der Videospur, ohne sie zu dekodieren.
/// </summary>
public sealed class SourceInfoCache
{
    private const int MaxEntries = 100_000;
    private const int MaxQueue = 20_000;
    private const int MaxBytes = 6 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    private static readonly Regex _crf = new(@"(?<![A-Za-z_\-])crf=(\d{1,2}(?:\.\d{1,3})?)", RegexOptions.CultureInvariant);
    private static readonly Regex _bitrate = new(@"(?<![A-Za-z_\-])bitrate=(\d{1,7})", RegexOptions.CultureInvariant);
    private static readonly Regex _qp = new(@"(?<![A-Za-z_\-])qp=(\d{1,2})", RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SourceInfoCache> _logger;
    private readonly string _file;
    private readonly Dictionary<string, SourceInfo> _known;
    private readonly Queue<(string Key, Guid Id)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private bool _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceInfoCache"/> class.
    /// </summary>
    /// <param name="store">Der Job-Speicher (kennt den Datenordner).</param>
    /// <param name="libraryManager">Die Bibliothek.</param>
    /// <param name="mediaEncoder">Der Encoder-Dienst.</param>
    /// <param name="logger">Der Logger.</param>
    public SourceInfoCache(JobStore store, ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILogger<SourceInfoCache> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _file = Path.Combine(store.DataDirectory, "sourceinfo.json");
        _known = Load();
    }

    /// <summary>
    /// Gets die Zahl der Dateien, die noch gelesen werden.
    /// </summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count + (_running ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// Liefert die gemerkten Angaben zu einer Datei.
    /// </summary>
    /// <param name="entry">Die Datei.</param>
    /// <param name="info">Die Angaben; ohne Encoder, wenn die Datei keine enthaelt.</param>
    /// <returns>False, solange die Datei noch nicht gelesen wurde.</returns>
    public bool TryGet(MediaEntry entry, out SourceInfo info)
    {
        if (!CanContainInfo(entry))
        {
            info = new SourceInfo();
            return true;
        }

        lock (_lock)
        {
            return _known.TryGetValue(Key(entry), out info!);
        }
    }

    /// <summary>
    /// Merkt Dateien zum Lesen vor. Gelesen wird nacheinander im Hintergrund.
    /// </summary>
    /// <param name="entries">Die Dateien.</param>
    /// <returns>Wie viele dieser Dateien noch nicht gelesen sind.</returns>
    public int Request(IEnumerable<MediaEntry> entries)
    {
        var missing = 0;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (!CanContainInfo(entry) || entry.Blocked is not null && entry.Size <= 0)
                {
                    continue;
                }

                var key = Key(entry);
                if (_known.ContainsKey(key))
                {
                    continue;
                }

                missing++;
                if (_queue.Count < MaxQueue && _queued.Add(key))
                {
                    _queue.Enqueue((key, entry.Id));
                }
            }

            if (_queue.Count > 0 && !_running)
            {
                _running = true;
                _ = Task.Run(WorkAsync);
            }
        }

        return missing;
    }

    private static bool CanContainInfo(MediaEntry entry) => entry.Codec is "h264" or "hevc";

    // Die Groesse gehoert zum Schluessel: wird die Datei ersetzt, wird neu gelesen.
    private static string Key(MediaEntry entry) => entry.Id.ToString("N", CultureInfo.InvariantCulture) + "|" + entry.Size.ToString(CultureInfo.InvariantCulture);

    private async Task WorkAsync()
    {
        var sinceSave = 0;
        while (true)
        {
            (string Key, Guid Id) next;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _running = false;
                    Save();
                    return;
                }

                next = _queue.Dequeue();
            }

            SourceInfo info;
            try
            {
                info = await ProbeAsync(next.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Eine nicht lesbare Datei bekommt einen leeren Eintrag, sonst wuerde sie bei jeder Anfrage erneut gelesen.
                _logger.LogDebug(ex, "Codec Converter: Encoder-Angaben nicht lesbar");
                info = new SourceInfo();
            }

            lock (_lock)
            {
                if (_known.Count >= MaxEntries)
                {
                    _known.Clear();
                }

                _known[next.Key] = info;
                _queued.Remove(next.Key);
                if (++sinceSave >= 25)
                {
                    sinceSave = 0;
                    Save();
                }
            }
        }
    }

    private async Task<SourceInfo> ProbeAsync(Guid id)
    {
        var path = _libraryManager.GetItemById(id)?.Path;
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ffmpeg) || !File.Exists(path))
        {
            return new SourceInfo();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        // Die ersten Bilder der Videospur unveraendert kopieren: die Angaben des Encoders stehen im ersten Bild.
        foreach (var argument in new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "quiet", "-protocol_whitelist", "file", "-i", "file:" + path,
            "-map", "0:V:0", "-c", "copy", "-frames:v", "4", "-f", "matroska", "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Nur eine Hoeflichkeit gegenueber laufenden Streams.
        }

        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var buffer = new byte[MaxBytes];
        var length = 0;
        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Schon beendet.
            }

            try
            {
                await errors.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Die Fehlerausgabe wird nicht gebraucht.
            }
        }

        return Parse(Encoding.Latin1.GetString(buffer, 0, length));
    }

    /// <summary>
    /// Sucht in den Rohdaten der Videospur die Einstellungszeile von x264 oder x265.
    /// </summary>
    /// <param name="data">Die Rohdaten als Text (ein Zeichen je Byte).</param>
    /// <returns>Die gefundenen Angaben.</returns>
    public static SourceInfo Parse(string data)
    {
        var info = new SourceInfo();
        var start = data.IndexOf("x264 - core", StringComparison.Ordinal);
        if (start >= 0)
        {
            info.Encoder = "x264";
        }
        else if ((start = data.IndexOf("x265 (build", StringComparison.Ordinal)) >= 0)
        {
            info.Encoder = "x265";
        }
        else
        {
            return info;
        }

        // Die Zeile endet am ersten Null-Byte, laenger als ein paar Tausend Zeichen ist sie nie.
        var end = data.IndexOf('\0', start);
        var options = data.Substring(start, Math.Min(end < 0 ? data.Length - start : end - start, 4000));

        var crf = _crf.Match(options);
        var abr = options.Contains("rc=abr", StringComparison.Ordinal) || options.Contains("rc=2pass", StringComparison.Ordinal) || options.Contains("rc=cbr", StringComparison.Ordinal);
        if (crf.Success && !abr
            && double.TryParse(crf.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 51)
        {
            info.Crf = value;
            return info;
        }

        var bitrate = _bitrate.Match(options);
        if (bitrate.Success && int.TryParse(bitrate.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var kbps) && kbps > 0)
        {
            info.TargetKbps = kbps;
            return info;
        }

        var qp = _qp.Match(options);
        if (qp.Success && options.Contains("rc=cqp", StringComparison.Ordinal)
            && int.TryParse(qp.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantizer) && quantizer <= 51)
        {
            info.Qp = quantizer;
        }

        return info;
    }

    private Dictionary<string, SourceInfo> Load()
    {
        try
        {
            if (File.Exists(_file) && new FileInfo(_file).Length < 64 * 1024 * 1024)
            {
                return JsonSerializer.Deserialize<Dictionary<string, SourceInfo>>(File.ReadAllText(_file)) ?? new(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nur ein Zwischenspeicher: wird bei Bedarf neu aufgebaut.
            _logger.LogWarning(ex, "Codec Converter: sourceinfo.json nicht lesbar, wird neu aufgebaut");
        }

        return new(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_known));
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Codec Converter: sourceinfo.json konnte nicht gespeichert werden");
        }
    }
}
