using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Storage;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Library;

/// <summary>The consumer message shown in place of the player, and whether trying again can change it.</summary>
public sealed record PlayerUnavailableView(string Title, string Body, bool OffersRetry);

/// <summary>
/// The web player of a Movie (<c>/Library/Watch/{workId}</c>) or one episode of a Series
/// (<c>/Library/Watch/{workId}/{episodeId}</c>). It resolves only the canonical Work/WorkEpisode target through the
/// shared <see cref="CanonicalVideoPlayerService"/> and renders the same player stage and scripts as every other video:
/// delivery stays in the playback plan, progress in the canonical <c>MediaProgress</c>.
/// </summary>
public sealed class WatchModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    CanonicalVideoPlayerService player,
    MediaAvailabilityService availability) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public string Title { get; private set; } = "";

    /// <summary>The player stage; null when the target is known but cannot be played, which shows <see cref="Unavailable"/> instead.</summary>
    public VideoPlayerStageView? Stage { get; private set; }

    /// <summary>What the page says instead of a player: no file at all, or a file the media tool could not check or rejected.</summary>
    public PlayerUnavailableView? Unavailable { get; private set; }

    public string BackUrl { get; private set; } = "";

    public string? EpisodeLine { get; private set; }

    public bool IsMovie { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid workId, Guid? episodeId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var work = await db.Works.AsNoTracking().Where(x => x.Id == workId).Select(x => new { x.MediaType, x.IsAnime, x.CanonicalTitle }).SingleOrDefaultAsync(cancellationToken);
        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        if (work is null || work.MediaType is not (WorkMediaType.Movie or WorkMediaType.Series) || !access.IsWorkVisible(work.MediaType, work.IsAnime))
        {
            return NotFound();
        }

        // A Movie is its own playable unit; a Series plays one of its episodes.
        if ((work.MediaType == WorkMediaType.Movie) != (episodeId is null))
        {
            return NotFound();
        }

        Title = work.CanonicalTitle;
        IsMovie = work.MediaType == WorkMediaType.Movie;
        BackUrl = LibraryBrowse.DetailHref(work.MediaType, workId);
        if (episodeId is { } id)
        {
            var episode = await db.WorkEpisodes.AsNoTracking().Where(x => x.Id == id && x.WorkId == workId).Select(x => new { x.SeasonNumber, x.EpisodeNumber, x.Title }).SingleOrDefaultAsync(cancellationToken);
            if (episode is null)
            {
                return NotFound();
            }

            EpisodeLine = string.IsNullOrWhiteSpace(episode.Title)
                ? AnimeDetailView.EpisodeCode(episode.SeasonNumber, episode.EpisodeNumber)
                : $"{AnimeDetailView.EpisodeCode(episode.SeasonNumber, episode.EpisodeNumber)} – {episode.Title}";
        }

        var target = episodeId is { } episodeTarget ? PlaybackVideoTarget.Episode(workId, episodeTarget) : PlaybackVideoTarget.Movie(workId);
        var outcome = await player.GetAsync(account.ProfileId, target, cancellationToken);
        if (outcome.Snapshot is { } snapshot)
        {
            Stage = await BuildStageAsync(snapshot, cancellationToken);
        }
        else
        {
            Unavailable = DescribeGap(outcome.Gap);
        }

        return Page();
    }

    // A media tool that could not run is not "no file": the viewer is told the file could not be prepared and may try again, while the
    // diagnosis (tool output) stays on the Admin file row. A file the tool rejected cannot be fixed by retrying.
    private PlayerUnavailableView DescribeGap(CanonicalVideoPlayerGap gap)
    {
        var prepareFailed = Ui[IsMovie ? "library.watch.prepareFailedMovie" : "library.watch.prepareFailedEpisode"];
        return gap switch
        {
            CanonicalVideoPlayerGap.AnalysisUnavailable => new PlayerUnavailableView(prepareFailed, Ui["library.watch.analysisPendingBody"], OffersRetry: true),
            CanonicalVideoPlayerGap.AnalysisRejected => new PlayerUnavailableView(prepareFailed, Ui["library.watch.analysisRejectedBody"], OffersRetry: false),
            _ => new PlayerUnavailableView(Ui["library.watch.noMediaTitle"], IsMovie ? Ui["library.video.noMediaBody"] : Ui["library.watch.noMediaBody"], OffersRetry: false)
        };
    }

    private async Task<VideoPlayerStageView> BuildStageAsync(CanonicalVideoPlayerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var storage = await availability.CheckMediaAsync(snapshot.File.StoredFileId, force: false, cancellationToken);
        var probe = PlaybackProbeResult.From(snapshot.Inventory.Technical!);
        var next = snapshot.Navigation.Next is { } item ? VideoDetailView.WatchHref(item.Target.WorkId, item.Target.WorkEpisodeId) : null;
        var previous = snapshot.Navigation.Previous;
        return new VideoPlayerStageView(
            Ui: Ui,
            LegacyEpisodeId: null,
            Target: snapshot.Target,
            PlanUrl: ClientApiRoutes.VideoPlaybackPlan,
            PlayerBootstrapUrl: ClientApiRoutes.VideoPlayer,
            ProgressUrl: ClientApiRoutes.VideoProgress,
            StorageAvailabilityUrl: ClientApiRoutes.MediaAvailability(snapshot.File.StoredFileId),
            StorageState: storage is null ? "unknown" : ClientApiMappings.AvailabilityStateName(storage.State),
            StorageHealth: storage is null ? "" : StorageHealth.Name(storage.Health),
            DurationSeconds: probe.DurationSeconds,
            ResumeSeconds: snapshot.Progress.ResumePositionMs / 1000d,
            NextUrl: next,
            AutoplayNext: snapshot.Preferences.AutoplayNext,
            OffersAutoplay: next is not null,
            FinishedKey: snapshot.Target.IsEpisode ? "library.episode.episodeFinished" : "library.video.finished",
            Title: Title,
            EpisodeLine: EpisodeLine,
            NativeTitle: null,
            BackUrl: BackUrl,
            BackLabelKey: "library.video.backToDetails",
            SubtitleCuesUrlTemplate: ClientApiRoutes.VideoSubtitleTrackCues(snapshot.Target, "__track__"),
            Controls: PlayerControls.Build(probe.Tracks, probe.VideoHeight, hasLearningCues: false, learningSourceStreamIndex: null, snapshot.Preferences),
            Cues: [],
            ShowPlayerTools: false,
            PreviousUrl: previous is null ? null : VideoDetailView.WatchHref(previous.Target.WorkId, previous.Target.WorkEpisodeId),
            PreviousLabel: previous is null ? null : Ui.Format("library.episode.previousEpisodeAria", ("season", previous.SeasonNumber.ToString("00")), ("episode", previous.EpisodeNumber.ToString("00"))));
    }
}
