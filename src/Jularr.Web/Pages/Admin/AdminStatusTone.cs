using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Library;

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

    public static string Of(AdminMediaState state) =>
        state switch
        {
            AdminMediaState.Available => "success",
            AdminMediaState.Missing => "warning",
            AdminMediaState.Failed => "danger",
            _ => "info"
        };

    public static string Of(ProviderConnectionState state) =>
        state switch
        {
            ProviderConnectionState.Healthy => "success",
            ProviderConnectionState.Unknown => "info",
            ProviderConnectionState.Disabled => "text",
            ProviderConnectionState.NotConfigured or ProviderConnectionState.Degraded => "warning",
            _ => "danger"
        };

    /// <summary>The outline glyph that backs the label of a provider state, so the state never rests on colour alone; empty when the label is enough.</summary>
    public static string IconOf(ProviderConnectionState state) =>
        state switch
        {
            ProviderConnectionState.Healthy => "check",
            ProviderConnectionState.Disabled => "power",
            ProviderConnectionState.Unknown => "",
            _ => "alert"
        };

    public static string KeyOf(ProviderConnectionState state) =>
        state switch
        {
            ProviderConnectionState.NotConfigured => "admin.providers.status.notConfigured",
            ProviderConnectionState.Disabled => "admin.providers.status.disabled",
            ProviderConnectionState.Healthy => "admin.providers.status.healthy",
            ProviderConnectionState.Degraded => "admin.providers.status.degraded",
            ProviderConnectionState.AuthenticationFailed => "admin.providers.status.authFailed",
            ProviderConnectionState.Unavailable => "admin.providers.status.unavailable",
            _ => "admin.providers.status.unknown"
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
