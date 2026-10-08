using System.Data.Common;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Owner page: the request queue from all users, who may use the manual add tools, the auto-approval
/// rules and the quality profiles requesters may pick. Who may request or add at once is not set here;
/// that is the capability matrix (Admin → Media capabilities).
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class RequestsModel(
    AppDbContext db,
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    AcquisitionRequestSettingsStore settings,
    QualityProfileStore qualityProfiles,
    VideoRequestWorkResolver videoWorks,
    RequestProfileAssignment profileAssignment,
    RequestArtworkResolver artwork,
    ILogger<RequestsModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    private const string PagePath = "/Admin/Requests";
    private const int QueueLimit = 2000;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<AcquisitionAccessPolicy> Policies { get; private set; } = [];
    public AdminRequestPage Queue { get; private set; } = AdminRequestQuery.Build([], new AdminRequestFilter(), new Dictionary<string, string>());

    /// <summary>Whether the queue could not be read; the rules below it still work.</summary>
    public bool QueueFailed { get; private set; }

    /// <summary>Whether the server has any request at all, whatever the filters say.</summary>
    public bool AnyRequests { get; private set; }

    /// <summary>The profiles that made requests, for the requester filter, by name.</summary>
    public IReadOnlyList<(string Id, string Name)> Requesters { get; private set; } = [];
    public IReadOnlyDictionary<string, string> ProfileNames { get; private set; } = new Dictionary<string, string>();
    public AcquisitionRequestSettings RequestSettings { get; private set; } = AcquisitionRequestSettings.Default;
    public IReadOnlyList<QualityProfile> QualityProfiles { get; private set; } = [];
    public IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; private set; } =
        Enum.GetValues<MediaAcquisitionKind>();
    public IReadOnlyDictionary<string, string> QualityProfileNames { get; private set; } = new Dictionary<string, string>();

    /// <summary>Whether the signed-in account may change rules and settings (the queue itself needs only the page policy).</summary>
    public bool CanEditSettings => JularrPolicies.Allows(User, JularrPolicies.AcquisitionSettings);

    /// <summary>The canonical Work of each Movie and TV request on the page, by request id; a request whose Work does not exist has no entry.</summary>
    public IReadOnlyDictionary<Guid, VideoRequestWork> VideoWorks { get; private set; } = new Dictionary<Guid, VideoRequestWork>();

    /// <summary>The poster of each request on the page, by request id, from the canonical artwork of its title; a request without artwork has no entry.</summary>
    public IReadOnlyDictionary<Guid, string> Posters { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>
    /// Where "View media" goes: the canonical Library page of a Movie or Series Work, never a search or a path, and the
    /// result address of the other media types. Null when there is nothing to open yet.
    /// </summary>
    public string? ViewUrl(AcquisitionRequest request) => request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv
        ? VideoWorks.TryGetValue(request.Id, out var work) ? VideoWorkLinks.DetailPath(request.Kind, work.WorkId) : null
        : request.ResultUrl;

    /// <summary>The requester's chosen options besides the audio language, which has its own column.</summary>
    public IReadOnlyList<string> OptionsOf(AcquisitionRequest request) =>
        RequestOptionsSummary.Describe(request.Options with { AudioLanguage = null }, Ui, QualityProfileNames);

    /// <summary>The address of the queue with the given filter; default values stay out of it.</summary>
    public static string Href(AdminRequestFilter filter)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        Add("tab", filter.Tab == AdminRequestTab.All ? null : AdminRequestQuery.TabName(filter.Tab));
        Add("type", filter.Kind is { } kind ? AcquisitionAccessNames.Kind(kind) : null);
        Add("status", filter.Status is { } status ? AcquisitionAccessNames.Status(status) : null);
        Add("lang", filter.Language);
        Add("by", filter.RequesterProfileId);
        Add("q", filter.Search?.Trim());
        Add("p", filter.Page > 1 ? filter.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        return parts.Count == 0 ? PagePath : $"{PagePath}?{string.Join('&', parts)}";
    }

    public async Task OnGetAsync(
        string? tab,
        string? type,
        string? status,
        string? lang,
        string? by,
        string? q,
        int p,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadEnabledKindsAsync(cancellationToken);
        Policies = (await store.GetPoliciesAsync(cancellationToken))
            .Where(policy => EnabledKinds.Contains(policy.Kind))
            .ToArray();
        ProfileNames = await db.OwnerAccounts
            .AsNoTracking()
            .ToDictionaryAsync(account => account.Id, account => account.UserName, cancellationToken);
        RequestSettings = await settings.LoadAsync(cancellationToken);
        QualityProfiles = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles;
        QualityProfileNames = QualityProfiles.ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.Ordinal);

        var filter = new AdminRequestFilter(
            AdminRequestQuery.ParseTab(tab),
            AdminRequestQuery.TryParseKind(type),
            AdminRequestQuery.TryParseStatus(status),
            string.IsNullOrWhiteSpace(lang) ? null : lang.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(by) ? null : by,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            Math.Max(p, 1));
        try
        {
            var rows = (await store.ListAllAsync(QueueLimit, cancellationToken))
                .Where(row => EnabledKinds.Contains(row.Kind))
                .ToArray();
            AnyRequests = rows.Length > 0;
            Requesters = rows
                .Select(row => row.RequestedByProfileId)
                .Distinct(StringComparer.Ordinal)
                .Select(id => (Id: id, Name: ProfileNames.GetValueOrDefault(id) ?? id))
                .OrderBy(requester => requester.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            Queue = AdminRequestQuery.Build(rows, filter, ProfileNames);
            VideoWorks = await videoWorks.ResolveAsync(Queue.Items, cancellationToken);
            Posters = await artwork.ResolvePostersAsync(Queue.Items, User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException)
        {
            logger.LogError(exception, "The request queue could not be read.");
            QueueFailed = true;
            Queue = AdminRequestQuery.Build([], filter, ProfileNames);
        }
    }

    public async Task<IActionResult> OnPostPoliciesAsync(CancellationToken cancellationToken)
    {
        await LoadEnabledKindsAsync(cancellationToken);
        if (!CanEditSettings)
        {
            return Forbid();
        }

        foreach (var kind in EnabledKinds)
        {
            var manual = Request.Form[$"manual.{AcquisitionAccessNames.Kind(kind)}"].ToString();
            if (manual.Length == 0)
            {
                continue;
            }

            await store.SavePolicyAsync(
                new AcquisitionAccessPolicy(kind, AcquisitionAccessNames.ParseManual(manual)),
                cancellationToken);
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["admin.requests.policiesSaved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddRuleAsync(
        string? name,
        string[]? kinds,
        string? profileId,
        int? maxRequests,
        int? periodDays,
        CancellationToken cancellationToken)
    {
        await LoadEnabledKindsAsync(cancellationToken);
        if (!CanEditSettings)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            // The limit needs both numbers or neither; a half-filled limit is a mistake, not "no limit".
            var quota = maxRequests is null && periodDays is null
                ? null
                : new AutoApprovalQuota(maxRequests ?? 0, periodDays ?? 0);
            var parsedKinds = (kinds ?? [])
                .Select(AcquisitionAccessNames.ParseKind)
                .ToArray();
            if (parsedKinds.Any(kind => !EnabledKinds.Contains(kind)))
            {
                throw new ArgumentException("A disabled media module cannot be added to an auto-approval rule.");
            }

            var rule = AutoApprovalRule.Create(
                name,
                parsedKinds,
                string.IsNullOrWhiteSpace(profileId) ? [] : [profileId],
                quota);
            await settings.AddRuleAsync(rule, cancellationToken);
            TempData["Status"] = ui["admin.requests.autoSaved"];
        }
        catch (ArgumentException)
        {
            TempData["Status"] = ui["admin.requests.autoInvalid"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        await settings.SetRuleEnabledAsync(id, enabled, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveRuleAsync(string id, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        await settings.RemoveRuleAsync(id, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProfilesAsync(string[]? profiles, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        // Only profiles that exist can be opened to requests.
        var known = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        await settings.SetRequesterQualityProfilesAsync((profiles ?? []).Where(known.Contains), cancellationToken);

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["admin.requests.profilesSaved"];
        return RedirectToPage();
    }

    /// <summary>Whether the owner can pick the profile while approving: the request must still wait, and have a Work the profile can be assigned to.</summary>
    public bool CanChooseProfile(AcquisitionRequest request) =>
        request.Status == AcquisitionRequestStatus.Pending && (RequestWorkBinder.Applies(request.Kind) || VideoWorks.ContainsKey(request.Id));

    public async Task<IActionResult> OnPostApproveAsync(Guid id, string? returnUrl, string? profileId, CancellationToken cancellationToken)
    {
        // Approve, retry and "search now" are one action; a Movie or TV request runs through the same executor as every other kind.
        if (!await IsManageableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        // The profile is assigned before the approval starts the search, so the first search already resolves it.
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var chosen = await profileAssignment.AssignAsync((await store.GetAsync(id, cancellationToken))!, profileId.Trim(), cancellationToken);
            if (chosen != RequestProfileResult.Assigned)
            {
                Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
                TempData["Status"] = Ui[chosen == RequestProfileResult.UnknownProfile ? "admin.requests.profileUnknown" : "admin.requests.profileUnavailable"];
                return Back(returnUrl);
            }
        }

        var request = await requests.ApproveAsync(id, cancellationToken);
        TempData["Status"] = request.StatusMessage ?? request.Title;
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, string? note, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!await IsManageableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        await requests.RejectAsync(id, note, cancellationToken);
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!await IsManageableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        await requests.MarkCompletedAsync(id, cancellationToken);
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostReopenAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!await IsManageableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        var request = await requests.ReopenAsync(id, cancellationToken);
        if (request.Status != AcquisitionRequestStatus.Pending)
        {
            var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = ui["admin.requests.reopenBlocked"];
        }

        return Back(returnUrl);
    }

    /// <summary>Whether the request exists and its media module is on; the queue does not list the others, so no action may reach them.</summary>
    private async Task<bool> IsManageableAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadEnabledKindsAsync(cancellationToken);
        return await store.GetAsync(id, cancellationToken) is { } request && EnabledKinds.Contains(request.Kind);
    }

    private async Task LoadEnabledKindsAsync(CancellationToken cancellationToken)
    {
        if (instanceModules is null)
        {
            EnabledKinds = Enum.GetValues<MediaAcquisitionKind>();
            return;
        }

        var instance = await instanceModules.GetAsync(cancellationToken);
        EnabledKinds = Enum.GetValues<MediaAcquisitionKind>()
            .Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            .ToArray();
    }

    /// <summary>Returns to the filtered queue the action came from; anything else goes to the unfiltered page.</summary>
    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && Url.IsLocalUrl(returnUrl)
        && (returnUrl == PagePath || returnUrl.StartsWith(PagePath + "?", StringComparison.Ordinal))
            ? LocalRedirect(returnUrl)
            : RedirectToPage();
}
