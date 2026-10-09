using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Admin → Wanted: the acquisition worklist. What is approved and not in the library yet, what is
/// missing, what is being searched and what failed, for every media type that tracks it. Searching
/// again reuses the request service and the anime acquisition scheduler; approving and rejecting
/// stay on Requests.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class WantedModel(
    AppDbContext db,
    WantedListService wanted,
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    AnimeAcquisitionScheduler scheduler,
    RequestArtworkResolver artwork,
    ILogger<WantedModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public const string PagePath = "/Admin/Wanted";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public AdminWantedPage List { get; private set; } = AdminWantedQuery.Build([], new AdminWantedFilter());

    /// <summary>Whether the worklist could not be read.</summary>
    public bool Failed { get; private set; }

    /// <summary>Whether anything at all is wanted, whatever the filters say.</summary>
    public bool AnyWanted { get; private set; }

    /// <summary>The cached poster of each Movie or Series Work on the page, by Work id.</summary>
    public IReadOnlyDictionary<Guid, string> WorkPosters { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>The cover of a row: the canonical Work poster of a Movie or Series, the cover the source carries for everything else.</summary>
    public string? CoverOf(WantedRow item) => item.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv
        ? item.WorkId is { } workId ? WorkPosters.GetValueOrDefault(workId) : null
        : item.CoverUrl;

    /// <summary>The moment the page was built; ages are counted from it.</summary>
    public DateTime NowUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>The address of the list with the given filter; default values stay out of it.</summary>
    public static string Href(AdminWantedFilter filter)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        Add("tab", filter.Tab == AdminWantedTab.All ? null : AdminWantedQuery.TabName(filter.Tab));
        Add("type", filter.Kind is { } kind ? AcquisitionAccessNames.Kind(kind) : null);
        Add("lang", filter.Language);
        Add("profile", filter.ProfileId);
        Add("q", filter.Search?.Trim());
        Add("sort", filter.Sort == AdminWantedSort.LastSearch ? null : AdminWantedQuery.SortName(filter.Sort));
        Add("p", filter.Page > 1 ? filter.Page.ToString(CultureInfo.InvariantCulture) : null);
        return parts.Count == 0 ? PagePath : $"{PagePath}?{string.Join('&', parts)}";
    }

    /// <summary>The unit a row is about: "Season 2, episode 13", "Volume 42", the requested seasons, or nothing for a whole title.</summary>
    public string? UnitLabel(WantedRow item)
    {
        if (item.Scope == RequestScope.Seasons && item.Selection is { } seasons)
        {
            return Ui.Format("requests.options.seasons", ("list", seasons));
        }

        if (item.Scope == RequestScope.Episodes && item.Selection is { } episodes)
        {
            return Ui.Format("requests.options.episodes", ("list", episodes));
        }

        if (item.Season is { } season && item.Episode is { } episode)
        {
            return Ui.Format(
                "admin.wanted.unit.episode",
                ("season", season.ToString(CultureInfo.InvariantCulture)),
                ("episode", episode.ToString(CultureInfo.InvariantCulture)));
        }

        if (item.Season is { } wholeSeason)
        {
            return Ui.Format("admin.wanted.unit.season", ("season", wholeSeason.ToString(CultureInfo.InvariantCulture)));
        }

        if (item.Volume is { } volume)
        {
            return Ui.Format("admin.wanted.unit.volume", ("volume", volume.ToString(CultureInfo.InvariantCulture)));
        }

        return item.ChapterStart switch
        {
            { } start when item.ChapterEnd is { } end && end > start =>
                Ui.Format("admin.wanted.unit.chapters", ("start", Number(start)), ("end", Number(end))),
            { } start => Ui.Format("admin.wanted.unit.chapter", ("chapter", Number(start))),
            _ => null
        };
    }

    /// <summary>How long ago a moment was: "Just now", "5 min ago", "3 h ago", "2 d ago".</summary>
    public string AgeLabel(DateTime thenUtc)
    {
        var (unit, count) = AdminWantedQuery.AgeOf(thenUtc, NowUtc);
        var number = count.ToString(CultureInfo.InvariantCulture);
        return unit switch
        {
            WantedAgeUnit.Minutes => Ui.Format("admin.wanted.age.minutes", ("count", number)),
            WantedAgeUnit.Hours => Ui.Format("admin.wanted.age.hours", ("count", number)),
            WantedAgeUnit.Days => Ui.Format("admin.wanted.age.days", ("count", number)),
            _ => Ui["admin.wanted.age.now"]
        };
    }

    public static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public async Task OnGetAsync(
        string? tab,
        string? type,
        string? lang,
        string? profile,
        string? q,
        string? sort,
        int p,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        NowUtc = DateTime.UtcNow;

        var filter = new AdminWantedFilter(
            AdminWantedQuery.ParseTab(tab),
            AdminWantedQuery.TryParseKind(type),
            string.IsNullOrWhiteSpace(lang) ? null : lang.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(profile) ? null : profile.Trim(),
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            AdminWantedQuery.ParseSort(sort),
            Math.Max(p, 1));
        try
        {
            var instance = instanceModules is null
                ? InstanceModuleSettings.Default
                : await instanceModules.GetAsync(cancellationToken);
            var items = (await wanted.LoadAsync(cancellationToken))
                .Where(item => instance.IsEnabled(AcquisitionInstanceModules.For(item.Kind)))
                .ToArray();
            AnyWanted = items.Length > 0;
            List = AdminWantedQuery.Build(items, filter);
            WorkPosters = await artwork.ResolveWorkPostersAsync([.. List.Items.Where(item => item.WorkId is not null).Select(item => item.WorkId!.Value)], User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException or JsonException or IOException or InvalidDataException)
        {
            logger.LogError(exception, "The wanted list could not be read.");
            Failed = true;
            List = AdminWantedQuery.Build([], filter);
        }
    }

    /// <summary>Searches an approved or failed request again right away (the same path as Approve on Requests).</summary>
    public async Task<IActionResult> OnPostSearchRequestAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var request = await store.GetAsync(id, cancellationToken);
        if (request is null)
        {
            TempData["Status"] = Ui["admin.wanted.requestGone"];
        }
        else if (!await IsKindEnabledAsync(request.Kind, cancellationToken))
        {
            return NotFound();
        }
        else if (request.Status is AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed)
        {
            var result = await requests.ApproveAsync(id, cancellationToken);
            TempData["Status"] = string.IsNullOrWhiteSpace(result.StatusMessage) ? result.Title : result.StatusMessage;
        }

        return Back(returnUrl);
    }

    /// <summary>Queues a search for the wanted episodes of one anime.</summary>
    public async Task<IActionResult> OnPostSearchAnimeAsync(string? animeKey, string? returnUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animeKey))
        {
            return BadRequest();
        }

        if (!await IsKindEnabledAsync(MediaAcquisitionKind.Anime, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = scheduler.RequestRun(animeKey.Trim())
            ? Ui["admin.wanted.searchQueued"]
            : Ui["acquisition.error.tooManyQueued"];
        return Back(returnUrl);
    }

    private async Task<bool> IsKindEnabledAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken) =>
        instanceModules is null
        || (await instanceModules.GetAsync(cancellationToken))
            .IsEnabled(AcquisitionInstanceModules.For(kind));

    /// <summary>Returns to the filtered list the action came from; anything else goes to the unfiltered list.</summary>
    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && Url.IsLocalUrl(returnUrl)
        && (returnUrl == PagePath || returnUrl.StartsWith(PagePath + "?", StringComparison.Ordinal))
            ? LocalRedirect(returnUrl)
            : RedirectToPage();
}
