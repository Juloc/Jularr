using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.InstantPlay;

/// <summary>
/// Resolves the capability chain of the Instant Play contract for the signed-in profile: instance modules, then the profile's
/// request capability and auto-approval (<see cref="AcquisitionRequestService.GetCapabilitiesAsync"/>), then acquisition health.
/// Whether Playback is available is only the instance switch; the profile's visibility of a media type is enforced at the page and API
/// boundary before this runs.
/// </summary>
public sealed class InstantPlayPolicyService(IInstanceModuleService modules, AcquisitionRequestService requests, VideoAcquisitionEngine acquisition)
{
    public async Task<InstantPlayPolicy> ResolveAsync(WorkMediaType mediaType, CancellationToken cancellationToken)
    {
        var instance = await modules.GetAsync(cancellationToken);
        var capabilities = await requests.GetCapabilitiesAsync(VideoWorkLinks.AcquisitionKind(mediaType), cancellationToken);
        var playbackEnabled = instance.IsEnabled(InstanceModule.Playback);

        // Setup health only decides between "Start watching" and "Request", so it is not read when instant acquisition is out of reach anyway.
        var acquisitionReady = playbackEnabled
            && capabilities is { CanRequest: true, AutoApproves: true }
            && await acquisition.FindSetupProblemAsync(cancellationToken) == VideoAcquisitionSetupProblem.None;
        return new InstantPlayPolicy(
            instance.IsEnabled(InstanceModuleMedia.For(mediaType)),
            instance.IsEnabled(InstanceModule.Acquisition),
            playbackEnabled,
            capabilities.CanRequest,
            capabilities.AutoApproves,
            acquisitionReady);
    }
}
