using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CodecConverter.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CodecConverter.Api;

/// <summary>
/// API fuer die Konfigurationsseite. Nur fuer Administratoren.
/// </summary>
[ApiController]
[Route("CodecConverter")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CodecConverterController : ControllerBase
{
    private const int MaxItemsPerRequest = 5000;
    private const int MaxJobsPerResponse = 300;

    private readonly LibraryIndex _index;
    private readonly JobStore _store;
    private readonly JobRunner _runner;
    private readonly LiveLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodecConverterController"/> class.
    /// </summary>
    /// <param name="index">Der Bibliotheksindex.</param>
    /// <param name="store">Die Warteschlange.</param>
    /// <param name="runner">Der Job-Dienst.</param>
    /// <param name="log">Das Live-Protokoll.</param>
    public CodecConverterController(LibraryIndex index, JobStore store, JobRunner runner, LiveLog log)
    {
        _log = log;
        _index = index;
        _store = store;
        _runner = runner;
    }

    /// <summary>
    /// Liefert Zielcodecs, Encoder und Qualitaetsstufen.
    /// </summary>
    /// <returns>Die Optionen.</returns>
    [HttpGet("Options")]
    public ActionResult GetOptions()
    {
        var targets = EncoderCatalog.Targets.Select(t => new
        {
            t.Codec,
            t.Label,
            t.MinQuality,
            t.MaxQuality,
            t.Presets,
            Encoders = t.Encoders.Select(e => new { e.Name, e.Label, e.Hardware, Available = _runner.SupportsEncoder(e.Name) })
        });

        return Ok(new
        {
            Targets = targets,
            EncoderCatalog.Speeds,
            JobRunner.CanPause
        });
    }

    /// <summary>
    /// Liefert alle Filme und Folgen mit ihren Codec-Daten.
    /// </summary>
    /// <param name="refresh">Zwischenspeicher verwerfen.</param>
    /// <returns>Die Eintraege.</returns>
    [HttpGet("Items")]
    public ActionResult GetItems([FromQuery] bool refresh = false)
    {
        var open = _store.OpenItemStates();
        var entries = _index.GetEntries(refresh).Select(e =>
        {
            // Der Zwischenspeicher wird geteilt, deshalb den Warteschlangen-Status nur in der Antwort setzen.
            var copy = (MediaEntry)CloneEntry(e);
            if (open.TryGetValue(e.Id, out var state))
            {
                if (state == JobState.AwaitingApproval)
                {
                    copy.Blocked ??= JobRunner.AwaitingApprovalText;
                }
                else
                {
                    copy.Queued = true;
                }
            }

            return copy;
        });
        return Ok(entries);
    }

    /// <summary>
    /// Schaetzt die Groesse nach der Umwandlung.
    /// </summary>
    /// <param name="request">Auswahl und Zieleinstellungen.</param>
    /// <returns>Die Schaetzung.</returns>
    [HttpPost("Estimate")]
    public ActionResult Estimate([FromBody] ConvertRequest request)
    {
        var error = Check(request, out var target, out var encoder);
        if (error is not null)
        {
            return BadRequest(new { Message = error });
        }

        var wanted = request.ItemIds.ToHashSet();
        var speeds = JobRunner.BuildSpeedTable(_store.Snapshot());
        long source = 0;
        long estimated = 0;
        var count = 0;
        double seconds = 0;
        var timeKnown = true;
        foreach (var entry in _index.GetEntries(false).Where(e => wanted.Contains(e.Id) && e.Blocked is null))
        {
            source += entry.Size;
            estimated += EncoderCatalog.EstimateSize(entry, target!, encoder!, request.Quality);
            count++;

            // Rechenzeit aus der Geschwindigkeit frueherer Jobs mit demselben Encoder.
            var speed = JobRunner.ExpectedSpeed(speeds, encoder!.Name, request.Speed, entry.Width, entry.Height, entry.Fps);
            if (speed is null || entry.DurationSeconds <= 0)
            {
                timeKnown = false;
            }
            else
            {
                seconds += entry.DurationSeconds / speed.Value;
            }
        }

        return Ok(new { Count = count, SourceBytes = source, EstimatedBytes = estimated, EstimatedSeconds = timeKnown && count > 0 ? seconds : (double?)null });
    }

    /// <summary>
    /// Legt Jobs fuer die Auswahl an.
    /// </summary>
    /// <param name="request">Auswahl und Zieleinstellungen.</param>
    /// <returns>Angelegte und uebersprungene Dateien.</returns>
    [HttpPost("Jobs")]
    public ActionResult CreateJobs([FromBody] ConvertRequest request)
    {
        var error = Check(request, out var target, out var encoder);
        if (error is null && !_runner.SupportsEncoder(encoder!.Name))
        {
            error = "Jellyfins ffmpeg kennt den Encoder " + encoder.Name + " nicht.";
        }

        if (error is not null)
        {
            return BadRequest(new { Message = error });
        }

        return Ok(_runner.Plan(request, target!, encoder!));
    }

    /// <summary>
    /// Liefert die Warteschlange und den Stream-Status.
    /// </summary>
    /// <returns>Der Stand.</returns>
    [HttpGet("Jobs")]
    public ActionResult GetJobs()
    {
        var all = _store.Snapshot();
        var speeds = JobRunner.BuildSpeedTable(all);
        var etas = new Dictionary<string, double?>(all.Count);
        double queueSeconds = 0;
        var queueKnown = true;
        var pendingCount = 0;
        foreach (var job in all)
        {
            var pending = job.State is JobState.Queued or JobState.Running or JobState.Paused;
            var eta = pending ? JobRunner.EstimateSeconds(job, speeds) : null;
            etas[job.Id] = eta;
            if (pending)
            {
                pendingCount++;
                queueKnown &= eta.HasValue;
                queueSeconds += eta ?? 0;
            }
        }

        // Die Seite fragt alle paar Sekunden ab: bei sehr langen Listen nur das Wichtige schicken. Zuerst alles, was
        // laeuft oder eine Entscheidung braucht, dann die Wartenden in ihrer Reihenfolge, dann die neuesten Abgeschlossenen.
        static int Rank(Job j) => j.State switch
        {
            JobState.Running or JobState.Paused or JobState.Finalizing => 0,
            JobState.AwaitingApproval => 1,
            JobState.Queued => 2,
            _ => 3
        };

        var shown = all
            .OrderBy(Rank)
            .ThenBy(j => j.State == JobState.Queued ? j.Created.Ticks : -j.Created.Ticks)
            .Take(MaxJobsPerResponse)
            .ToList();

        return Ok(new
        {
            _runner.ActiveStreams,
            _runner.StreamLimit,
            _runner.IsBlocked,

            // Restliche Rechenzeit der ganzen Warteschlange; null, solange fuer einen Job noch kein Erfahrungswert da ist.
            QueueEtaSeconds = pendingCount > 0 && queueKnown ? queueSeconds : (double?)null,
            Total = all.Count,
            OpenCount = all.Count(j => j.State is JobState.Queued or JobState.Running or JobState.Paused or JobState.Finalizing),
            AwaitingCount = all.Count(j => j.State == JobState.AwaitingApproval),
            Jobs = shown.Select(j => new
            {
                j.Id,
                j.Name,
                j.SourceCodec,
                j.TargetCodec,
                j.Encoder,
                j.Quality,
                State = j.State.ToString(),
                j.Progress,
                j.EncodeSpeed,
                EtaSeconds = etas[j.Id],
                j.ActiveSeconds,
                j.SourceSize,
                j.EstimatedSize,
                j.ResultSize,
                j.Message,
                j.Created,
                j.Finished,
                SourceName = Path.GetFileName(j.SourcePath),
                TargetName = Path.GetFileName(j.TargetPath),

                // Zeitpunkt der automatischen Freigabe, falls eine Frist gesetzt ist.
                j.SourceErrors,
                AutoApproveAt = j.State == JobState.AwaitingApproval && j.Converted.HasValue && _runner.AutoApproveDays > 0 && j.SourceErrors == 0
                    ? j.Converted.Value.AddDays(_runner.AutoApproveDays)
                    : (DateTime?)null
            })
        });
    }

    /// <summary>
    /// Liefert die Zeilen des Live-Protokolls nach einer Nummer.
    /// </summary>
    /// <param name="after">Die zuletzt gesehene Nummer (0 = alles Vorhandene).</param>
    /// <returns>Die neuen Zeilen.</returns>
    [HttpGet("Log")]
    public ActionResult GetLog([FromQuery] long after = 0)
    {
        return Ok(new { Lines = _log.Since(after) });
    }

    /// <summary>
    /// Bricht einen Job ab bzw. entfernt einen abgeschlossenen Job aus der Liste.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <returns>Ergebnis.</returns>
    [HttpDelete("Jobs/{id}")]
    public ActionResult DeleteJob([FromRoute] string id)
    {
        var error = _runner.CancelOrRemove(id);
        return error is null ? NoContent() : BadRequest(new { Message = error });
    }

    /// <summary>
    /// Gibt eine umgewandelte Datei frei: das Original wird geloescht.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <returns>Ergebnis.</returns>
    [HttpPost("Jobs/{id}/Approve")]
    public async Task<ActionResult> Approve([FromRoute] string id, CancellationToken cancellationToken)
    {
        var error = await _runner.ApproveAsync(id, false, cancellationToken).ConfigureAwait(false);
        return error is null ? NoContent() : BadRequest(new { Message = error });
    }

    /// <summary>
    /// Verwirft eine umgewandelte Datei: die neue Datei wird geloescht, das Original bleibt.
    /// </summary>
    /// <param name="id">Die Job-Id.</param>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <returns>Ergebnis.</returns>
    [HttpPost("Jobs/{id}/Reject")]
    public async Task<ActionResult> Reject([FromRoute] string id, CancellationToken cancellationToken)
    {
        var error = await _runner.RejectAsync(id, cancellationToken).ConfigureAwait(false);
        return error is null ? NoContent() : BadRequest(new { Message = error });
    }

    /// <summary>
    /// Gibt alle wartenden Jobs frei.
    /// </summary>
    /// <param name="cancellationToken">Abbruch-Token.</param>
    /// <returns>Anzahl und Fehler.</returns>
    [HttpPost("Jobs/ApproveAll")]
    public async Task<ActionResult> ApproveAll(CancellationToken cancellationToken)
    {
        var (approved, errors) = await _runner.ApproveAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { Approved = approved, Errors = errors });
    }

    /// <summary>
    /// Entfernt alle abgeschlossenen Jobs aus der Liste.
    /// </summary>
    /// <returns>Anzahl der entfernten Jobs.</returns>
    [HttpPost("Jobs/ClearFinished")]
    public ActionResult ClearFinished()
    {
        // Jobs, deren Gesehen-Status noch auf die neue Datei zu uebertragen ist, bleiben bis dahin stehen.
        return Ok(new { Removed = _store.Remove(j => !JobStore.IsOpen(j) && j.PendingUserStates.Count == 0) });
    }

    private static object CloneEntry(MediaEntry e) => new MediaEntry
    {
        Id = e.Id,
        Type = e.Type,
        Name = e.Name,
        Year = e.Year,
        SeriesId = e.SeriesId,
        SeriesName = e.SeriesName,
        Season = e.Season,
        Episode = e.Episode,
        Codec = e.Codec,
        Width = e.Width,
        Height = e.Height,
        Fps = e.Fps,
        VideoBitrate = e.VideoBitrate,
        Size = e.Size,
        DurationSeconds = e.DurationSeconds,
        Container = e.Container,
        FileName = e.FileName,
        VideoRange = e.VideoRange,
        Blocked = e.Blocked
    };

    private static string? Check(ConvertRequest? request, out TargetCodec? target, out EncoderInfo? encoder)
    {
        target = null;
        encoder = null;
        if (request is null || request.ItemIds is null || request.ItemIds.Count == 0)
        {
            return "Es ist nichts ausgewählt.";
        }

        if (request.ItemIds.Count > MaxItemsPerRequest)
        {
            return "Höchstens " + MaxItemsPerRequest + " Dateien pro Anfrage.";
        }

        return EncoderCatalog.Validate(request, out target, out encoder);
    }
}
