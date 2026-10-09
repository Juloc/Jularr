using Jularr.Web.Data;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class UserModel(
    AppDbContext db,
    OwnerAuthService authService,
    AniListAccountStore aniListAccountStore,
    AdminSessionsService sessionsService,
    PlaybackStreamSessionStore sessionStore,
    KnownDeviceRegistry deviceRegistry,
    MediaCapabilityStore? mediaCapabilities = null,
    AcquisitionRequestSettingsStore? requestSettings = null,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public LocalAccountSummary Account { get; private set; } = null!;

    public IReadOnlyList<AdminSessionRow> Sessions { get; private set; } = [];

    public IReadOnlyList<KnownDeviceRow> Devices { get; private set; } = [];

    public bool ShowRequestPolicy { get; private set; }

    public bool RequestPolicyFailed { get; private set; }

    public IReadOnlyList<UserRequestCapabilityRow> RequestCapabilities { get; private set; } = [];

    public IReadOnlyList<UserAutoApprovalRuleRow> AutoApprovalRules { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(
        string id,
        string userName,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.RenameAsync(id, userName, cancellationToken);
            TempData["Status"] = Ui["admin.user.nameUpdated"];
            return RedirectToPage(new { id });
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostSetEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.SetEnabledAsync(id, enabled, cancellationToken);
            TempData["Status"] = enabled
                ? Ui["admin.user.enabled"]
                : Ui["admin.user.disabledSessionsRevoked"];
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostSetRoleAsync(
        string id,
        AccountRole role,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.SetRoleAsync(id, role, cancellationToken);
            TempData["Status"] = Ui["admin.users.roleSaved"];
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(
        string id,
        string newPassword,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.ResetPasswordAsync(id, newPassword, cancellationToken);
            TempData["Status"] = Ui["admin.user.passwordReset"];
            return RedirectToPage(new { id });
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostInvalidateSessionsAsync(
        string id,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.InvalidateSessionsAsync(id, cancellationToken);
            TempData["Status"] = Ui["admin.user.sessionsSignedOut"];
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostStopSessionAsync(
        string id,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await authService.GetAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        TempData["Status"] = sessionStore.Remove(sessionId, id)
            ? Ui["profile.devices.stopped"]
            : Ui["profile.devices.alreadyEnded"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRevokeDeviceAsync(
        string id,
        string deviceId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await authService.GetAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        TempData["Status"] = await deviceRegistry.RevokeAsync(
            deviceId,
            requesterProfileId: id,
            cancellationToken)
            ? Ui["admin.devices.revoked"]
            : Ui["admin.devices.alreadyGone"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        string id,
        string confirmation,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var account = await authService.GetAsync(id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        if (!string.Equals(
                account.UserName,
                confirmation?.Trim(),
                StringComparison.Ordinal))
        {
            ModelState.AddModelError(
                string.Empty,
                Ui["admin.user.confirmationRequired"]);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }

        try
        {
            await authService.DeleteUserAsync(id, cancellationToken);
            await aniListAccountStore.DisconnectAsync(id, cancellationToken);
            TempData["Status"] = Ui.Format("admin.user.deleted", ("userName", account.UserName));
            return RedirectToPage("/Admin/Users");
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await ReloadOrNotFoundAsync(id, cancellationToken);
        }
    }

    private async Task<IActionResult> ReloadOrNotFoundAsync(
        string id,
        CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken)
            ? Page()
            : NotFound();

    private async Task<bool> LoadAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var account = await authService.GetAsync(id, cancellationToken);
        if (account is null)
        {
            return false;
        }

        Account = account;
        Sessions = await sessionsService.ListForProfileAsync(id, cancellationToken);
        Devices = await deviceRegistry.ListForProfileAsync(id, cancellationToken);
        await LoadRequestPolicyAsync(account, cancellationToken);
        return true;
    }

    private async Task LoadRequestPolicyAsync(
        LocalAccountSummary account,
        CancellationToken cancellationToken)
    {
        if (mediaCapabilities is null || requestSettings is null)
        {
            return;
        }

        try
        {
            var instance = instanceModules is null
                ? InstanceModuleSettings.Default
                : await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition))
            {
                return;
            }

            var enabledKinds = Enum.GetValues<MediaAcquisitionKind>()
                .Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
                .ToArray();
            if (enabledKinds.Length == 0)
            {
                return;
            }

            var policy = await mediaCapabilities.LoadAsync(cancellationToken);
            RequestCapabilities = enabledKinds
                .Select(kind => new UserRequestCapabilityRow(
                    kind,
                    policy.Resolve(
                        account.Role,
                        account.Id,
                        AcquisitionAccessNames.WorkType(kind))))
                .ToArray();

            var settings = await requestSettings.LoadAsync(cancellationToken);
            AutoApprovalRules = settings.AutoApprovalRules
                .Where(rule =>
                    rule.ProfileIds.Count == 0
                    || rule.ProfileIds.Contains(account.Id, StringComparer.Ordinal))
                .Where(rule =>
                    rule.Kinds.Count == 0
                    || rule.Kinds.Any(enabledKinds.Contains))
                .Select(rule => new UserAutoApprovalRuleRow(
                    rule.Id,
                    rule.Name,
                    rule.Enabled,
                    rule.Kinds,
                    rule.ProfileIds.Count == 0,
                    rule.Quota))
                .ToArray();

            ShowRequestPolicy = true;
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException)
        {
            RequestPolicyFailed = true;
            ShowRequestPolicy = true;
        }
    }

    public sealed record UserRequestCapabilityRow(
        MediaAcquisitionKind Kind,
        MediaCapability Capability);

    public sealed record UserAutoApprovalRuleRow(
        string Id,
        string Name,
        bool Enabled,
        IReadOnlyList<MediaAcquisitionKind> Kinds,
        bool AppliesToEveryone,
        AutoApprovalQuota? Quota);
}
