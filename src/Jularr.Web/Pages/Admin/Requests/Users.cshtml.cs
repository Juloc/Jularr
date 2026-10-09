using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Requests;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class UsersModel(AppDbContext db, OwnerAuthService accounts, AcquisitionRequestSettingsStore settings, MediaCapabilityStore capabilities, IInstanceModuleService instanceModules) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public LocalAccountPage Directory { get; private set; } = new([], 0, 1, 50);
    public LocalAccountSummary? SelectedUser { get; private set; }
    public AcquisitionRequestSettings RequestSettings { get; private set; } = AcquisitionRequestSettings.Default;
    public MediaCapabilityPolicy Capabilities { get; private set; } = MediaCapabilityPolicy.Default;
    public IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; private set; } = [];
    public IReadOnlyList<MediaAcquisitionKind> EditableKinds { get; private set; } = [];
    public string Query { get; private set; } = string.Empty;
    public bool IsUserSettings => Request.Path.StartsWithSegments("/Admin/Users");
    [BindProperty] public RequestRuleEditorInput Editor { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string? userId, string? q, int p, CancellationToken cancellationToken)
    {
        await LoadAsync(userId, q, p, cancellationToken);
        if (userId is not null && SelectedUser is null || IsUserSettings && SelectedUser is null)
        {
            return NotFound();
        }

        if (SelectedUser is { } user)
        {
            var rule = RequestSettings.Rules.Resolve(user.Id, RequestSettings.AutoApprovalRules);
            Editor = RequestRuleEditorInput.FromProfile(rule.Profile, RequestSettings.Revision, rule);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveUserAsync(string userId, string? q, int p, CancellationToken cancellationToken)
    {
        await LoadAsync(userId, q, p, cancellationToken);
        if (SelectedUser is null)
        {
            return NotFound();
        }

        try
        {
            if (!ModelState.IsValid)
            {
                throw new ArgumentException("Invalid request rule input.");
            }

            var assignment = RequestSettings.Rules.Users.GetValueOrDefault(userId);
            var profile = RequestSettings.Rules.Profiles.SingleOrDefault(profile => profile.Id == (Editor.RuleId ?? RequestSettings.Rules.DefaultId)) ?? throw new ArgumentException("Unknown request rule.");
            var inherited = (assignment?.Overrides ?? new RequestRuleOverrides()).Apply(profile.Values);
            var edited = Editor.UseOverrides ? Editor.ReadValues(EditableKinds, inherited.Kinds) : profile.Values;
            await settings.SaveUserRuleAsync(userId, Editor.RuleId, edited, Editor.UseOverrides, Editor.Revision, cancellationToken);
            TempData["Status"] = Ui["requestRules.saved"];
            return Redirect(IsUserSettings ? $"/Admin/Users/{Uri.EscapeDataString(userId)}/Settings/Requests" : $"/Admin/Requests/Users?userId={Uri.EscapeDataString(userId)}&q={Uri.EscapeDataString(Query)}&p={Directory.Page}");
        }
        catch (Exception exception) when (exception is ArgumentException or RequestRuleConflictException)
        {
            ModelState.AddModelError(string.Empty, Ui[exception is RequestRuleConflictException ? "requestRules.conflict" : "requestRules.invalid"]);
            return Page();
        }
    }

    private async Task LoadAsync(string? userId, string? q, int page, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = (q ?? string.Empty).Trim();
        if (Query.Length > 80)
        {
            Query = Query[..80];
        }

        RequestSettings = await settings.LoadAsync(cancellationToken);
        Capabilities = await capabilities.LoadAsync(cancellationToken);
        var instance = await instanceModules.GetAsync(cancellationToken);
        EnabledKinds = Enum.GetValues<MediaAcquisitionKind>().Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind))).ToArray();
        if (!IsUserSettings)
        {
            Directory = await accounts.ListPageAsync(Query, page, cancellationToken);
        }

        SelectedUser = userId is null ? null : await accounts.GetAsync(userId, cancellationToken);
        if (SelectedUser is { } user)
        {
            EditableKinds = EnabledKinds.Where(kind => Capabilities.Resolve(user.Role, user.Id, AcquisitionAccessNames.WorkType(kind)) >= MediaCapability.Request).ToArray();
        }
    }
}
