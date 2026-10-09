using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Access;

public enum RequestProfileResult
{
    Assigned,
    UnknownProfile,
    NoWork,
    StateChanged
}

/// <summary>
/// The profile an owner chooses while approving a request. It is the same per-Work assignment the Work page edits (<see cref="QualityProfileStore.AssignWorkAsync"/>),
/// made on the Work the request stands for. Anime keeps its explicit choice in the canonical request options instead of a parallel assignment path.
/// </summary>
public sealed class RequestProfileAssignment(QualityProfileStore profiles, RequestWorkBinder binder, VideoRequestWorkResolver videoWorks, AcquisitionAccessStore store)
{
    public async Task<RequestProfileResult> AssignAsync(AcquisitionRequest request, string profileId, CancellationToken cancellationToken)
    {
        if (!(await profiles.LoadAsync(cancellationToken)).Profiles.Any(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return RequestProfileResult.UnknownProfile;
        }

        if (request.Kind == MediaAcquisitionKind.Anime)
        {
            return await store.PatchPayloadAsync(request.Id, stored => (AcquisitionRequestOptions.FromPayload(stored) with { QualityProfileId = profileId }).Validate().ToPayloadJson(), request.Status, request.Status, null, cancellationToken)
                ? RequestProfileResult.Assigned
                : RequestProfileResult.StateChanged;
        }

        var workId = await WorkOfAsync(request, cancellationToken);
        if (workId is null)
        {
            return RequestProfileResult.NoWork;
        }

        await profiles.AssignWorkAsync(workId.Value, profileId, cancellationToken);
        return RequestProfileResult.Assigned;
    }

    private async Task<Guid?> WorkOfAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (RequestWorkBinder.Applies(request.Kind))
        {
            return (await binder.EnsureBoundAsync(request, cancellationToken)).WorkId;
        }

        return (await videoWorks.ResolveAsync([request], cancellationToken)).TryGetValue(request.Id, out var work) ? work.WorkId : null;
    }
}
