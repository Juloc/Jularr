using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>The canonical Work, effective payload and episode a Manual Search of one Movie/TV request targets.</summary>
public sealed record VideoManualTarget(
    Guid WorkId,
    string Title,
    int? Year,
    VideoRequestPayload Payload,
    VideoUnit? Unit,
    IReadOnlyList<VideoUnit> MissingUnits,
    QualityProfile Profile,
    bool HasLocalFile,
    bool UnitChanged = false);

// Manual Search entry points of the shared Movie/TV engine. They reuse the search, scoring and grab code of automatic acquisition
// so the owner sees exactly what the scheduler would see and a manual grab is submitted through the same download path.
public sealed partial class VideoAcquisitionEngine
{
    public static string SetupProblemMessage(VideoAcquisitionSetupProblem problem) =>
        problem switch
        {
            VideoAcquisitionSetupProblem.NoIndexer => "No Usenet indexer is configured.",
            VideoAcquisitionSetupProblem.NoDownloadClient => "No download client is configured.",
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

    public async Task<VideoAcquisitionSetupProblem> FindSetupProblemAsync(CancellationToken cancellationToken)
    {
        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return VideoAcquisitionSetupProblem.NoIndexer;
        }

        return (await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled)
            ? VideoAcquisitionSetupProblem.None
            : VideoAcquisitionSetupProblem.NoDownloadClient;
    }

    /// <summary>
    /// Resolves what Manual Search looks for: the canonical Work of the request and, for TV, one missing aired episode. A requested
    /// episode that is no longer missing is reported as <see cref="VideoManualTarget.UnitChanged"/> with no episode, so a stale link
    /// never silently searches or grabs another one; without a request the episode the request is working on, then the next missing one.
    /// Returns null when the canonical Work behind the request no longer exists.
    /// </summary>
    public async Task<VideoManualTarget?> ResolveManualTargetAsync(AcquisitionRequest request, Guid? requestedUnitId, CancellationToken cancellationToken)
    {
        if (request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            throw new ArgumentException("Video Manual Search only supports Movie and TV requests.", nameof(request));
        }

        var target = await ResolveTargetAsync(request, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var payload = VideoRequestPayload.Of(request, target.WorkId, target.Title, target.Year);
        var profile = await profiles.ResolveAsync(request.Kind, target.WorkId, cancellationToken);
        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            return new VideoManualTarget(target.WorkId, target.Title, target.Year, payload, null, [], profile, await HasMovieFileAsync(target.WorkId, cancellationToken));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var missing = (await LoadTvUnitsAsync(target.WorkId, cancellationToken)).Where(x => !x.HasFile && (x.AiredAt is null || x.AiredAt <= now)).ToArray();
        if (requestedUnitId is not null)
        {
            var requested = missing.FirstOrDefault(x => x.Id == requestedUnitId);
            return new VideoManualTarget(target.WorkId, target.Title, target.Year, payload, requested, missing, profile, HasLocalFile: false, UnitChanged: requested is null);
        }

        var unit = missing.FirstOrDefault(x => x.Id == payload.ActiveWorkEpisodeId)
                   ?? await FindNextTvUnitAsync(request, payload, cancellationToken)
                   ?? missing.FirstOrDefault();
        return new VideoManualTarget(target.WorkId, target.Title, target.Year, payload, unit, missing, profile, HasLocalFile: false);
    }

    /// <summary>Searches the configured indexers for the target and evaluates every candidate with the profile of the Work.</summary>
    public Task<VideoSearchEvaluation> SearchManualAsync(AcquisitionRequest request, VideoManualTarget target, CancellationToken cancellationToken) =>
        SearchAndEvaluateAsync(request.Kind, target.Payload, target.Unit, target.Profile, cancellationToken);

    /// <summary>
    /// Submits the one release the owner selected through the shared grab path. The caller has already verified that it is grabbable
    /// and not yet tried; the tracker records it as tried so neither automatic acquisition nor a second selection submits it again.
    /// </summary>
    public Task<AcquisitionExecution> GrabManualAsync(AcquisitionRequest request, VideoManualTarget target, VideoReleaseEvaluation selected, VideoGrabProgress progress, CancellationToken cancellationToken)
    {
        if (!selected.IsGrabbable)
        {
            throw new InvalidOperationException("The selected release is not eligible.");
        }

        var payload = target.Unit is null ? target.Payload : WithActiveUnit(target.Payload, target.Unit);
        return GrabAsync(request, payload, target.Unit, [selected], "The selected release is no longer available.", cancellationToken, progress);
    }
}
