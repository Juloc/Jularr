using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Sonarr;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// The owner's grab of one interactive search result: the anime's request is claimed (a request with a grab in flight is not claimable, so nothing is grabbed twice),
/// the release goes through the same core as an automatic grab, and the Wanted pass follows and imports it. Ownership always applies; a
/// rejected release may be chosen because the owner picked it.
/// </summary>
public sealed class AnimeManualGrabService(
    AnimeAcquisitionPipeline pipeline,
    AnimeAcquisitionEngine engine,
    AnimeRequestStarter starter,
    ManualGrabCoordinator coordinator,
    AcquisitionCore core,
    SonarrObservationService observation,
    TimeProvider clock)
{
    public async Task<AnimeGrabResult> GrabAsync(string animeKey, int? seasonNumber, int? episodeNumber, ProwlarrAnimeSearchMode mode, string releaseIdentity, CancellationToken cancellationToken)
    {
        var search = await pipeline.SearchInteractiveAsync(animeKey, seasonNumber, episodeNumber, mode, cancellationToken);
        if (search is null)
        {
            return new AnimeGrabResult(false, "Anime not found.");
        }

        if (search.Error is not null)
        {
            return new AnimeGrabResult(false, search.Error);
        }

        var candidate = search.Candidates.FirstOrDefault(item => item.Release.Identity.Equals(releaseIdentity, StringComparison.OrdinalIgnoreCase));
        var evaluation = search.Evaluations?.FirstOrDefault(item => item.Candidate.Identity.Equals(releaseIdentity, StringComparison.OrdinalIgnoreCase));
        if (candidate is null || evaluation is null)
        {
            return new AnimeGrabResult(false, "The release is no longer offered by the indexers; search again.");
        }

        if (!candidate.Release.IsAcquirable)
        {
            return new AnimeGrabResult(false, "The release has no NZB link.");
        }

        var episodes = candidate.CoveredEpisodes.Count > 0 ? candidate.CoveredEpisodes : search.Episode is { } key ? [key] : [];
        if (episodes.Count == 0)
        {
            return new AnimeGrabResult(false, "The release does not cover an episode of this anime.");
        }

        var now = clock.GetUtcNow();
        var release = candidate.Release.ParsedRelease;
        var ownership = SonarrParallelSafety.CanGrab(
            await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken),
            new AcquisitionGrabRequest(
                animeKey,
                release.ReleaseKey,
                release.SeasonNumber ?? episodes[0].SeasonNumber,
                release.EpisodeStart ?? episodes[0].EpisodeNumber,
                release.EpisodeEnd ?? episodes[^1].EpisodeNumber,
                release.AbsoluteEpisodeStart ?? episodes[0].AbsoluteEpisodeNumber,
                release.AbsoluteEpisodeEnd ?? episodes[^1].AbsoluteEpisodeNumber),
            now);
        if (!ownership.Allowed)
        {
            return new AnimeGrabResult(false, $"Ownership: {ownership.Reason}");
        }

        if (await starter.EnsureRequestAsync(animeKey, cancellationToken) is not var (request, _))
        {
            return new AnimeGrabResult(false, "The anime needs an AniList match before it can be downloaded.");
        }

        var label = AnimeAcquisitionPipeline.Label(episodes[0]);
        var outcome = await coordinator.GrabAsync(
            request,
            [AcquisitionRequestStatus.Approved],
            async (claimed, progress) =>
            {
                var payload = AnimeRequestPayload.Of(claimed) with
                {
                    AnimeKey = animeKey,
                    Episodes = episodes,
                    ReleaseIdentity = evaluation.Candidate.Identity,
                    ReleaseTitle = evaluation.Candidate.Title,
                    TriedReleases = (AnimeRequestPayload.Of(claimed).TriedReleases ?? []).Where(identity => !identity.Equals(evaluation.Candidate.Identity, StringComparison.OrdinalIgnoreCase)).ToArray()
                };
                var execution = await core.GrabAsync(claimed, payload, [evaluation], "The release is no longer offered.", new GrabTarget(AnimeAcquisitionEngine.OperationKind, "Anime download", $"{search.Target.Anime.Title} · {label}", MediaAcquisitionKind.Anime, animeKey), cancellationToken, progress);
                if (execution.Status == AcquisitionRequestStatus.Downloading && execution.OperationId is { } operationId)
                {
                    await engine.RecordGrabAsync(search.Target, episodes, evaluation, operationId, now, cancellationToken);
                }

                return execution with { ResultUrl = $"/Library/Anime/{search.Target.Anime.Id}" };
            },
            cancellationToken);
        return outcome.Status switch
        {
            ManualGrabStatus.Submitted => new AnimeGrabResult(true, $"Sent to SABnzbd: {evaluation.Candidate.Title}", outcome.Request?.OperationId),
            ManualGrabStatus.NotSearchable => new AnimeGrabResult(false, "A download for this anime is already running; wait for it to finish."),
            ManualGrabStatus.AlreadySubmitted => new AnimeGrabResult(false, "This release was already sent."),
            _ => new AnimeGrabResult(false, outcome.Message ?? "SABnzbd did not accept the release.")
        };
    }
}
