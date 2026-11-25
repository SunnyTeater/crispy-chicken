using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Momentaufnahme eines laufenden Jobs fuer die Messung.
/// </summary>
/// <param name="Id">Die Job-Id.</param>
/// <param name="CpuSeconds">Bisher verbrauchte CPU-Zeit von ffmpeg in Sekunden (alle Threads zusammen).</param>
/// <param name="EncodedSeconds">Bisher umgewandelte Laufzeit in Sekunden.</param>
/// <param name="PixelRate">Bildpunkte pro Sekunde Laufzeit (Breite x Hoehe x Bildrate).</param>
/// <param name="Paused">Ob der Job gerade angehalten ist.</param>
/// <param name="Hardware">Ob der Job einen Hardware-Encoder benutzt.</param>
/// <param name="MemoryBytes">Arbeitsspeicher von ffmpeg.</param>
public sealed record WorkerSample(string Id, double CpuSeconds, double EncodedSeconds, double PixelRate, bool Paused, bool Hardware, long MemoryBytes);

/// <summary>
/// Entscheidung, ob ein weiterer Job starten soll.
/// </summary>
/// <param name="Start">Ob gestartet wird.</param>
/// <param name="Reason">Begruendung fuer das Live-Protokoll.</param>
public sealed record ParallelDecision(bool Start, string Reason);

/// <summary>
/// Misst die Auslastung des Servers und entscheidet, ob ein weiterer Job gleichzeitig laufen soll.
/// Ein weiterer Job startet nur, wenn genug Threads frei sind, der Arbeitsspeicher reicht und bisher kein
/// Versuch gezeigt hat, dass ein zusaetzlicher Job den Durchsatz kaum erhoeht.
/// </summary>
public sealed class ParallelMonitor
{
    /// <summary>Nach jeder Aenderung (Start, Ende, Pause) so lange warten, bis die Werte stabil sind.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(45);

    /// <summary>Ueber diesen Zeitraum wird gemessen.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    // Bringt ein weiterer Job weniger als 8 % mehr Durchsatz, lohnt er sich nicht.
    private const double MinGain = 0.08;
    private const long MemoryReserve = 1024L * 1024 * 1024;
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CapDuration = TimeSpan.FromHours(2);
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(15);

    // Deutsche Zahlen ohne Abhaengigkeit von installierten Sprachdaten (Jellyfin kann ohne ICU laufen).
    private static readonly NumberFormatInfo German = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    private readonly Func<DateTime> _now;
    private readonly Func<string?> _procStat;
    private readonly Func<long?> _availableMemory;
    private readonly List<Sample> _samples = new();

    // Gesamtauslastung fuer die Anzeige, unabhaengig davon, ob gerade ein Job startet oder endet.
    private readonly List<(DateTime Time, double Busy, double Total)> _system = new();
    private readonly object _lock = new();
    private string _signature = string.Empty;
    private bool _anyPaused;
    private DateTime _stableSince;
    private Evaluation? _evaluation;
    private int? _cap;
    private DateTime _capUntil;

    // So viele Jobs gleichzeitig haben sich in dieser Warteschlange schon bewaehrt: wird einer fertig, startet der
    // naechste sofort, ohne erneut zu messen.
    private int? _proven;
    private long _lastJobMemory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParallelMonitor"/> class.
    /// </summary>
    /// <param name="threads">Anzahl der Threads, die Jellyfin nutzen darf.</param>
    /// <param name="now">Uhr.</param>
    /// <param name="procStat">Liefert den Inhalt von /proc/stat oder null.</param>
    /// <param name="availableMemory">Liefert den verfuegbaren Arbeitsspeicher oder null.</param>
    public ParallelMonitor(int threads, Func<DateTime> now, Func<string?> procStat, Func<long?> availableMemory)
    {
        Threads = Math.Max(1, threads);
        _now = now;
        _procStat = procStat;
        _availableMemory = availableMemory;
        _stableSince = now();
    }

    /// <summary>
    /// Gets die Anzahl der Threads, die Jellyfin nutzen darf (beachtet CPU-Zuweisung und Container-Grenzen).
    /// </summary>
    public int Threads { get; }

    /// <summary>
    /// Gets die letzte gemessene Gesamtauslastung in Prozent oder null.
    /// </summary>
    public double? CpuPercent { get; private set; }

    /// <summary>
    /// Gets die von den Jobs zuletzt genutzten Threads oder null.
    /// </summary>
    public double? JobThreads { get; private set; }

    /// <summary>
    /// Gets die gelernte Obergrenze gleichzeitiger Jobs oder null.
    /// </summary>
    public int? Cap
    {
        get
        {
            lock (_lock)
            {
                return _cap is not null && _now() < _capUntil ? _cap : null;
            }
        }
    }

    /// <summary>
    /// Gets die Anzahl gleichzeitiger Jobs, die sich in der laufenden Warteschlange bewaehrt hat, oder null.
    /// </summary>
    public int? Proven
    {
        get
        {
            lock (_lock)
            {
                return _proven;
            }
        }
    }

    /// <summary>
    /// Gets die hoechste Anzahl gleichzeitiger Jobs, die die Automatik je startet.
    /// </summary>
    public int HardLimit => Math.Clamp(Threads / 2, 2, 16);

    /// <summary>
    /// Erstellt die Messung fuer diesen Server.
    /// </summary>
    /// <returns>Die Messung.</returns>
    public static ParallelMonitor Create()
        => new(Environment.ProcessorCount, () => DateTime.UtcNow, ReadProcStat, ReadAvailableMemory);

    /// <summary>
    /// Nimmt eine Messung auf. Wird alle paar Sekunden aufgerufen.
    /// </summary>
    /// <param name="workers">Die laufenden Jobs.</param>
    /// <returns>Eine Meldung fuer das Live-Protokoll, wenn eine Durchsatz-Pruefung abgeschlossen wurde.</returns>
    public string? Add(IReadOnlyList<WorkerSample> workers)
    {
        lock (_lock)
        {
            var now = _now();
            var (busy, total, hostThreads) = ParseStat(_procStat());
            var signature = string.Join('|', workers.OrderBy(w => w.Id, StringComparer.Ordinal).Select(w => w.Id + (w.Paused ? "/angehalten" : string.Empty)));
            _anyPaused = workers.Any(w => w.Paused);
            if (!string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                // Start, Ende oder Pause: die bisherigen Werte gelten nicht mehr.
                _signature = signature;
                _stableSince = now;
                _samples.Clear();
            }

            _samples.Add(new Sample(
                now,
                workers.Sum(w => w.CpuSeconds),
                busy,
                total,
                hostThreads,
                workers.ToDictionary(w => w.Id, w => w.EncodedSeconds, StringComparer.Ordinal),
                workers.ToDictionary(w => w.Id, w => w.PixelRate, StringComparer.Ordinal),
                workers.Count == 0 ? 0 : workers.Max(w => w.MemoryBytes)));
            _samples.RemoveAll(s => s.Time < now - Keep);
            if (busy is not null && total is not null && hostThreads == Threads)
            {
                // Nur wenn Jellyfin den ganzen Rechner nutzen darf (sonst ist die Gesamtauslastung irrefuehrend).
                _system.Add((now, busy.Value, total.Value));
                _system.RemoveAll(s => s.Time < now - TimeSpan.FromMinutes(1));
            }

            if (workers.Count == 0)
            {
                // Warteschlange leer: beim naechsten Mal kann es ganz anders aussehen (andere Aufloesung, anderer Encoder).
                _evaluation = null;
                _cap = null;
                _proven = null;
            }

            var measurement = MeasureLocked(now, Settle);
            if (measurement is not null)
            {
                CpuPercent = measurement.CpuPercent;
                JobThreads = measurement.OurThreads;
            }
            else
            {
                if (workers.Count == 0)
                {
                    JobThreads = 0;
                }

                CpuPercent = MeasureSystemOnly(now) ?? (workers.Count == 0 ? null : CpuPercent);
            }

            return EvaluateLocked(now, workers);
        }
    }

    /// <summary>
    /// Entscheidet, ob ein weiterer Job starten soll.
    /// </summary>
    /// <param name="running">Anzahl laufender Jobs.</param>
    /// <param name="fixedLimit">Feste Anzahl aus den Einstellungen, 0 = automatisch.</param>
    /// <param name="nextHardware">Ob der naechste Job einen Hardware-Encoder benutzt.</param>
    /// <param name="hardwareRunning">Ob schon ein Job mit Hardware-Encoder laeuft.</param>
    /// <returns>Die Entscheidung.</returns>
    public ParallelDecision Decide(int running, int fixedLimit, bool nextHardware, bool hardwareRunning)
    {
        if (fixedLimit > 0)
        {
            return running < fixedLimit
                ? new ParallelDecision(true, "fest eingestellt: bis zu " + fixedLimit + " Jobs gleichzeitig")
                : new ParallelDecision(false, "fest eingestellte Anzahl erreicht");
        }

        if (running == 0)
        {
            return new ParallelDecision(true, "erster Job");
        }

        if (nextHardware && hardwareRunning)
        {
            // Grafikkarten begrenzen gleichzeitige Sitzungen; die Automatik startet dort nur einen Job.
            return new ParallelDecision(false, "die Grafikkarte ist schon belegt");
        }

        lock (_lock)
        {
            var now = _now();
            if (_cap is not null && now < _capUntil && running >= _cap)
            {
                return new ParallelDecision(false, "ein weiterer Job hat den Durchsatz zuletzt kaum erhöht");
            }

            if (running >= HardLimit)
            {
                return new ParallelDecision(false, "Höchstzahl gleichzeitiger Jobs erreicht");
            }

            if (_anyPaused)
            {
                return new ParallelDecision(false, "Jobs sind angehalten");
            }

            if (_proven is not null && running < _proven)
            {
                var free = _availableMemory();
                if (free is not null && _lastJobMemory > 0 && free.Value < (long)(_lastJobMemory * 1.5) + MemoryReserve)
                {
                    return new ParallelDecision(false, "zu wenig freier Arbeitsspeicher");
                }

                return new ParallelDecision(true, _proven + " Jobs gleichzeitig haben sich bewährt");
            }

            if (_evaluation is not null)
            {
                return new ParallelDecision(false, "der zuletzt gestartete Job wird noch gemessen");
            }

            var measurement = MeasureLocked(now, Settle);
            if (measurement is null)
            {
                return new ParallelDecision(false, "misst noch");
            }

            var perJob = measurement.OurThreads / running;
            _lastJobMemory = measurement.MaxMemory;
            var memory = _availableMemory();
            if (memory is not null && measurement.MaxMemory > 0 && memory.Value < (long)(measurement.MaxMemory * 1.5) + MemoryReserve)
            {
                return new ParallelDecision(false, "zu wenig freier Arbeitsspeicher");
            }

            // Ein weiterer Job braucht etwa so viel wie die laufenden; etwas mehr als die Haelfte davon muss frei sein,
            // mindestens aber 15 % des Servers. Hardware-Jobs brauchen wenig CPU.
            var needed = nextHardware
                ? Math.Max(1.0, Threads * 0.1)
                : Math.Max(Math.Max(perJob * 0.6, Threads * 0.15), 0.75);
            var text = string.Format(
                German,
                "{0:0.0} von {1} Threads frei, ein Job nutzt etwa {2:0.0}",
                measurement.FreeThreads,
                Threads,
                perJob);
            return measurement.FreeThreads >= needed
                ? new ParallelDecision(true, text)
                : new ParallelDecision(false, text);
        }
    }

    /// <summary>
    /// Meldet, dass ein Job gestartet wurde. Bei automatischer Wahl wird danach geprueft, ob er den Durchsatz erhoeht.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <param name="runningBefore">Anzahl der Jobs, die schon liefen.</param>
    /// <param name="automatic">Ob die Anzahl automatisch gewaehlt wird.</param>
    public void Started(string id, int runningBefore, bool automatic)
    {
        lock (_lock)
        {
            _evaluation = null;
            if (!automatic || runningBefore < 1)
            {
                return;
            }

            var measurement = MeasureLocked(_now(), Settle);
            if (measurement is not null && measurement.Throughput > 0)
            {
                _evaluation = new Evaluation(id, runningBefore, measurement.Throughput, _now());
            }
        }
    }

    private static (double? Busy, double? Total, int Threads) ParseStat(string? stat)
    {
        if (string.IsNullOrEmpty(stat))
        {
            return (null, null, 0);
        }

        double? busy = null;
        double? total = null;
        var threads = 0;
        foreach (var line in stat.Split('\n'))
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                    .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
                    .ToArray();
                if (values.Length >= 5)
                {
                    // user nice system idle iowait irq softirq steal
                    var idle = values[3] + values[4];
                    var used = values[0] + values[1] + values[2] + values.Skip(5).Take(3).Sum();
                    busy = used;
                    total = used + idle;
                }
            }
            else if (line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(line[3]))
            {
                threads++;
            }
        }

        return (busy, total, threads);
    }

    private static string? ReadProcStat()
    {
        try
        {
            return OperatingSystem.IsLinux() && File.Exists("/proc/stat") ? File.ReadAllText("/proc/stat") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadAvailableMemory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            long? available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)
                    && long.TryParse(line["MemAvailable:".Length..].Replace("kB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    available = kb * 1024;
                    break;
                }
            }

            // In einem Container mit Speichergrenze (cgroup v2) zaehlt die Grenze.
            if (File.Exists("/sys/fs/cgroup/memory.max") && File.Exists("/sys/fs/cgroup/memory.current"))
            {
                var max = File.ReadAllText("/sys/fs/cgroup/memory.max").Trim();
                if (long.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                    && long.TryParse(File.ReadAllText("/sys/fs/cgroup/memory.current").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
                {
                    var free = Math.Max(0, limit - current);
                    available = available is null ? free : Math.Min(available.Value, free);
                }
            }

            return available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private double? MeasureSystemOnly(DateTime now)
    {
        var recent = _system.Where(s => s.Time >= now - TimeSpan.FromSeconds(20)).ToList();
        if (recent.Count < 2 || recent[^1].Total - recent[0].Total <= 0)
        {
            return null;
        }

        return Math.Clamp(100 * (recent[^1].Busy - recent[0].Busy) / (recent[^1].Total - recent[0].Total), 0, 100);
    }

    // Misst ueber das letzte Fenster, sofern sich seit "settle" + Fenster nichts geaendert hat.
    private Measurement? MeasureLocked(DateTime now, TimeSpan settle)
    {
        if (now - _stableSince < settle + Window)
        {
            return null;
        }

        var window = _samples.Where(s => s.Time >= now - Window && s.Time >= _stableSince + settle).ToList();
        if (window.Count < 2)
        {
            return null;
        }

        var first = window[0];
        var last = window[^1];
        var seconds = (last.Time - first.Time).TotalSeconds;
        if (seconds < Window.TotalSeconds * 0.8)
        {
            return null;
        }

        var ourThreads = Math.Max(0, (last.OurCpu - first.OurCpu) / seconds);
        double otherThreads = 0;
        double cpuPercent = 100 * ourThreads / Threads;
        if (first.Busy is not null && last.Busy is not null && last.Total > first.Total)
        {
            var busyFraction = (last.Busy.Value - first.Busy!.Value) / (last.Total!.Value - first.Total!.Value);
            if (last.HostThreads == Threads)
            {
                // Was ausser den Jobs laeuft (Streams, andere Dienste), geht vor.
                otherThreads = Math.Max(0, (busyFraction * Threads) - ourThreads);
                cpuPercent = 100 * busyFraction;
            }
        }

        var throughput = last.Encoded.Sum(e => first.Encoded.TryGetValue(e.Key, out var before)
            ? Math.Max(0, e.Value - before) * last.PixelRate[e.Key]
            : 0) / seconds;

        return new Measurement(
            ourThreads,
            Math.Max(0, Threads - ourThreads - otherThreads),
            Math.Clamp(cpuPercent, 0, 100),
            throughput,
            window.Max(s => s.MaxMemory));
    }

    private string? EvaluateLocked(DateTime now, IReadOnlyList<WorkerSample> workers)
    {
        var evaluation = _evaluation;
        if (evaluation is null)
        {
            return null;
        }

        if (!workers.Any(w => string.Equals(w.Id, evaluation.Id, StringComparison.Ordinal)) || now - evaluation.Time > EvaluationTimeout)
        {
            // Job vorher fertig, abgebrochen oder zu lange keine ruhige Messung: nichts gelernt.
            _evaluation = null;
            return null;
        }

        if (workers.Count != evaluation.Before + 1)
        {
            return null;
        }

        var measurement = MeasureLocked(now, Settle);
        if (measurement is null)
        {
            return null;
        }

        _evaluation = null;
        var gain = (measurement.Throughput / evaluation.Throughput) - 1;
        var text = string.Format(
            German,
            "Durchsatz mit {0} statt {1} Jobs: {2:+0;-0} % (Auslastung {3:0} %).",
            evaluation.Before + 1,
            evaluation.Before,
            gain * 100,
            measurement.CpuPercent);
        if (gain >= MinGain)
        {
            _proven = Math.Max(_proven ?? 0, evaluation.Before + 1);
        }
        else
        {
            _cap = evaluation.Before;
            _capUntil = now + CapDuration;
            return text + " Lohnt sich kaum: die nächsten zwei Stunden laufen "
                + (evaluation.Before == 1 ? "keine Jobs parallel." : "höchstens " + evaluation.Before + " Jobs gleichzeitig.");
        }

        return text;
    }

    private sealed record Sample(
        DateTime Time,
        double OurCpu,
        double? Busy,
        double? Total,
        int HostThreads,
        Dictionary<string, double> Encoded,
        Dictionary<string, double> PixelRate,
        long MaxMemory);

    private sealed record Measurement(double OurThreads, double FreeThreads, double CpuPercent, double Throughput, long MaxMemory);

    private sealed record Evaluation(string Id, int Before, double Throughput, DateTime Time);
}
