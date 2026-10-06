using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaSegments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Library;

public sealed record EpisodeSegmentKindRow(
    MediaSegmentKind Kind,
    string Label,
    ResolvedMediaSegment? Resolved,
    EpisodeMediaSegment? Manual,
    IReadOnlyList<EpisodeMediaSegment> Candidates);

// Owner correction surface for one episode's skip markers. Manual markers win over
// imported, provider and detector markers; removing one falls back to the next source.
public sealed class EpisodeSegmentsModel(
    AppDbContext db,
    MediaSegmentService segments,
    CurrentAccountContext currentAccount,
    ILogger<EpisodeSegmentsModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public Guid EpisodeId { get; private set; }
    public Guid AnimeId { get; private set; }
    public string AnimeTitle { get; private set; } = "";
    public string EpisodeTitle { get; private set; } = "";
    public int SeasonNumber { get; private set; }
    public int EpisodeNumber { get; private set; }
    public double SkipConfidenceThreshold { get; private set; }
    public bool DetectorEnabled => segments.DetectorEnabled;
    public IReadOnlyList<EpisodeSegmentKindRow> Rows { get; private set; } = [];
    public TrickplayDescriptor Trickplay { get; private set; } = TrickplayDescriptor.Unavailable;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        return await LoadAsync(id, cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostSaveAsync(
        Guid id,
        MediaSegmentKind kind,
        string? start,
        string? end,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        if (!Enum.IsDefined(kind))
        {
            return BadRequest();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!MediaTimecode.TryParse(start, out var startMs) ||
            !MediaTimecode.TryParse(end, out var endMs))
        {
            TempData["Status"] = ui["library.episodeSegments.invalidTimecode"];
            return RedirectToPage(new { id });
        }

        try
        {
            var saved = await segments.SaveManualAsync(id, kind, startMs, endMs, cancellationToken);
            if (saved is null)
            {
                return NotFound();
            }
        }
        catch (ArgumentException exception)
        {
            logger.LogError(exception, "Saving {Kind} marker for episode {EpisodeId} failed", kind, id);
            TempData["Status"] = ui["library.episodeSegments.saveFailed"];
            return RedirectToPage(new { id });
        }

        TempData["Status"] = ui.Format(
            "library.episodeSegments.markerSaved",
            ("kind", KindLabel(ui, kind)));
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(
        Guid id,
        MediaSegmentKind kind,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var removed = await segments.RemoveManualAsync(id, kind, cancellationToken);
        TempData["Status"] = removed
            ? ui.Format("library.episodeSegments.markerRemoved", ("kind", KindLabel(ui, kind)))
            : ui["library.episodeSegments.noManualMarker"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDetectAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var run = await segments.RunDetectorAsync(id, force: true, cancellationToken);
        TempData["Status"] = run.Outcome switch
        {
            SegmentDetectionOutcome.DetectorDisabled => ui["library.episodeSegments.detectorDisabled"],
            SegmentDetectionOutcome.NoMedia => ui["library.episodeSegments.noMedia"],
            SegmentDetectionOutcome.NotAnalyzed => ui["library.episodeSegments.notAnalyzed"],
            _ => ui.Format("library.episodeSegments.detectionFinished", ("count", run.SegmentCount))
        };
        return RedirectToPage(new { id });
    }

    // Queues cross-episode OP/ED detection (audio fingerprinting) for every episode of this
    // episode's season as one bounded background operation; never runs inline with the request.
    public async Task<IActionResult> OnPostDetectSeasonAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var queued = await segments.QueueSeasonDetectionAsync(id, cancellationToken);
        TempData["Status"] = queued
            ? ui["library.episodeSegments.seasonDetectionQueued"]
            : ui["library.episodeSegments.seasonDetectionNotQueued"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRegeneratePreviewsAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var queued = await segments.RegenerateTrickplayAsync(id, cancellationToken);
        TempData["Status"] = queued
            ? ui["library.episodeSegments.previewsQueued"]
            : ui["library.episodeSegments.previewsNotQueued"];
        return RedirectToPage(new { id });
    }

    // Local mapping from the shared MediaSegmentKind enum to catalog text: MediaSegmentPolicy's
    // own KindLabel() also feeds the client API contract, so it intentionally stays English there.
    public static string KindLabel(UiTextBundle ui, MediaSegmentKind kind) => kind switch
    {
        MediaSegmentKind.Intro => ui["library.episodeSegments.kind.intro"],
        MediaSegmentKind.Recap => ui["library.episodeSegments.kind.recap"],
        MediaSegmentKind.Outro => ui["library.episodeSegments.kind.outro"],
        MediaSegmentKind.Preview => ui["library.episodeSegments.kind.preview"],
        MediaSegmentKind.Credits => ui["library.episodeSegments.kind.credits"],
        _ => MediaSegmentPolicy.KindLabel(kind)
    };

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var episode = await (
                from item in db.Episodes.AsNoTracking()
                join anime in db.Anime.AsNoTracking() on item.AnimeId equals anime.Id
                where item.Id == id
                select new
                {
                    item.Id,
                    item.AnimeId,
                    AnimeTitle = anime.Title,
                    item.Title,
                    item.SeasonNumber,
                    item.Number
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (episode is null)
        {
            return false;
        }

        EpisodeId = episode.Id;
        AnimeId = episode.AnimeId;
        AnimeTitle = episode.AnimeTitle;
        EpisodeTitle = episode.Title;
        SeasonNumber = episode.SeasonNumber;
        EpisodeNumber = episode.Number;

        var stored = await segments.ListAsync(id, cancellationToken);
        var resolved = await segments.GetSegmentsAsync(id, cancellationToken);
        SkipConfidenceThreshold = resolved.SkipConfidenceThreshold;
        Rows =
        [
            .. MediaSegmentPolicy.Kinds.Select(kind => new EpisodeSegmentKindRow(
                kind,
                KindLabel(Ui, kind),
                resolved.Segments.FirstOrDefault(x => x.Kind == kind),
                stored.FirstOrDefault(x => x.Kind == kind && x.Source == MediaSegmentSource.Manual),
                [.. stored.Where(x => x.Kind == kind)]))
        ];
        Trickplay = await segments.GetTrickplayAsync(id, cancellationToken);
        return true;
    }
}
