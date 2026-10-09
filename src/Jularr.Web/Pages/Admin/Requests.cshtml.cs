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
/// The permission-scoped operational request queue. Request configuration and manual-add policies have dedicated destinations.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class RequestsModel(
    AppDbContext db,
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    QualityProfileStore qualityProfiles,
    VideoRequestWorkResolver videoWorks,
    RequestArtworkResolver artwork,
    ILogger<RequestsModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    private const string PagePath = "/Admin/Requests";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public AdminRequestPage Queue { get; private set; } = AdminRequestQuery.Empty(new AdminRequestFilter());

    /// <summary>Whether the queue could not be read.</summary>
    public bool QueueFailed { get; private set; }

    /// <summary>Whether the server has any request at all, whatever the filters say.</summary>
    public bool AnyRequests { get; private set; }

    /// <summary>The profiles that made requests, for the requester filter, by name.</summary>
    public IReadOnlyList<(string Id, string Name)> Requesters { get; private set; } = [];
    public IReadOnlyDictionary<string, string> ProfileNames { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<QualityProfile> QualityProfiles { get; private set; } = [];
    public IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; private set; } =
        Enum.GetValues<MediaAcquisitionKind>();
    public IReadOnlyDictionary<string, string> QualityProfileNames { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<Guid, string> EffectiveProfileNames { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>Whether the signed-in account may change rules and settings (the queue itself needs only the page policy).</summary>
    public bool CanEditSettings => JularrPolicies.Allows(User, JularrPolicies.AcquisitionSettings);

    /// <summary>The canonical Work of each Movie and TV request on the page, by request id; a request whose Work does not exist has no entry.</summary>
    public IReadOnlyDictionary<Guid, VideoRequestWork> VideoWorks { get; private set; } = new Dictionary<Guid, VideoRequestWork>();

    public IReadOnlyDictionary<Guid, IReadOnlyList<int>> VideoSeasonNumbers { get; private set; } = new Dictionary<Guid, IReadOnlyList<int>>();

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

    public IReadOnlyList<string> CoverageOf(AcquisitionRequest request)
    {
        if (request.Kind == MediaAcquisitionKind.Anime)
        {
            return request.Options.Scope switch
            {
                RequestScope.Seasons => request.Options.Seasons.Order().Select(season => $"S{season}").ToArray(),
                RequestScope.Episodes => RequestOptionsSummary.Describe(request.Options with { AudioLanguage = null }, Ui, QualityProfileNames),
                _ => []
            };
        }

        if (request.Kind == MediaAcquisitionKind.Tv && VideoSeasonNumbers.TryGetValue(request.Id, out var seasons))
        {
            return seasons.Select(season => season == 0 ? Ui["admin.requests.filter.specials"] : $"S{season}").ToArray();
        }

        return OptionsOf(request);
    }

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
        Add("season", filter.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("q", filter.Search?.Trim());
        Add("sort", filter.Sort == "newest" ? null : filter.Sort);
        Add("p", filter.Page > 1 ? filter.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        Add("size", filter.PageSize != AdminRequestQuery.DefaultPageSize ? filter.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        return parts.Count == 0 ? PagePath : $"{PagePath}?{string.Join('&', parts)}";
    }

    public async Task OnGetAsync(
        string? tab,
        string? type,
        string? status,
        string? lang,
        string? by,
        string? q,
        string? sort,
        int? season,
        int p,
        int size,
        int customSize,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadEnabledKindsAsync(cancellationToken);
        ProfileNames = await db.OwnerAccounts
            .AsNoTracking()
            .ToDictionaryAsync(account => account.Id, account => account.UserName, cancellationToken);
        var qualityState = await qualityProfiles.LoadAsync(cancellationToken);
        QualityProfiles = qualityState.Profiles;
        QualityProfileNames = QualityProfiles.ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.Ordinal);

        var filter = new AdminRequestFilter(
            AdminRequestQuery.ParseTab(tab),
            AdminRequestQuery.TryParseKind(type),
            AdminRequestQuery.TryParseStatus(status),
            string.IsNullOrWhiteSpace(lang) ? null : lang.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(by) ? null : by,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            Math.Max(p, 1),
            AdminRequestQuery.NormalizePageSize(size == 0 ? customSize : size),
            AdminRequestQuery.NormalizeSort(sort),
            season);
        try
        {
            var result = await store.ReadQueueAsync(filter, EnabledKinds, ProfileNames, cancellationToken);
            Queue = result.Page;
            AnyRequests = result.AnyRequests;
            Requesters = result.RequesterIds.Select(id => (Id: id, Name: ProfileNames.GetValueOrDefault(id) ?? id)).OrderBy(requester => requester.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            VideoSeasonNumbers = await videoWorks.ResolveSeasonNumbersAsync(Queue.Items, cancellationToken);
            VideoWorks = await videoWorks.ResolveAsync(Queue.Items, cancellationToken);
            var effectiveProfiles = new Dictionary<Guid, string>();
            foreach (var request in Queue.Items)
            {
                var workId = request.WorkId ?? (VideoWorks.TryGetValue(request.Id, out var videoWork) ? videoWork.WorkId : (Guid?)null);
                var effective = request.Options.QualityProfileId ?? qualityState.ResolveProfileId(request.Kind, workId);
                effectiveProfiles[request.Id] = effective is not null ? QualityProfileNames.GetValueOrDefault(effective) ?? effective : "—";
            }

            EffectiveProfileNames = effectiveProfiles;
            Posters = await artwork.ResolvePostersAsync(Queue.Items, User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException)
        {
            logger.LogError(exception, "The request queue could not be read.");
            QueueFailed = true;
            Queue = AdminRequestQuery.Empty(filter);
        }
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
            var chosen = await requests.ChangeProfileAsync(id, profileId.Trim(), cancellationToken);
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
        if (note?.Length > 2000)
        {
            return BadRequest();
        }

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

    public async Task<IActionResult> OnPostBulkAsync(Guid[] ids, RequestBulkAction action, string? note, string? profileId, string? returnUrl, bool confirmed, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || ids is null || ids.Length is < 1 or > AcquisitionRequestService.MaxBulkRequests || !Enum.IsDefined(action) || note?.Length > 2000 || action == RequestBulkAction.Delete && !confirmed)
        {
            return BadRequest();
        }

        var results = await requests.BulkAsync(ids, action, note, profileId, cancellationToken);
        TempData["RequestActionResults"] = System.Text.Json.JsonSerializer.Serialize(results);
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
