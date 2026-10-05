using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// The one mapping from an acquisition state to the tone of its outlined Admin tag (<c>admin-tag-*</c> in admin-controls.css), so Requests,
/// Wanted and Manual Search never colour the same state differently. The label always names the state; the tone only supports it.
/// </summary>
public static class AdminStatusTone
{
    public static string Of(AcquisitionRequestStatus status) =>
        status switch
        {
            AcquisitionRequestStatus.Pending => "info",
            AcquisitionRequestStatus.Approved => "success",
            AcquisitionRequestStatus.Searching or AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing => "warning",
            AcquisitionRequestStatus.Completed => "accent",
            _ => "danger"
        };

    public static string Of(WantedStatus status) =>
        status switch
        {
            WantedStatus.Requested => "info",
            WantedStatus.Missing => "warning",
            WantedStatus.Failed => "danger",
            _ => "accent"
        };
}
