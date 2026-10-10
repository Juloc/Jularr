using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings.Indexers;

/// <summary>
/// Add or edit one canonical indexer. A new Newznab indexer needs only its address and API key: <see cref="IndexerSetupService"/> reads the
/// capabilities, maps the media types and checks that searching works. Everything else is advanced and optional.
/// </summary>
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class EditModel(AppDbContext db, IndexerStore store, IndexerSetupService setup, ILogger<EditModel> logger, IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public IndexerType Type { get; set; } = IndexerType.Newznab;

    [BindProperty]
    public string BaseUrl { get; set; } = "";

    [BindProperty]
    public string? ApiKey { get; set; }

    /// <summary>The categories the owner typed per media type name; a blank entry uses what the indexer advertises.</summary>
    [BindProperty(Name = "KindOverrides")]
    public Dictionary<string, string?> KindOverrides { get; set; } = [];

    /// <summary>The media types this indexer is searched for; none selected means every media type.</summary>
    [BindProperty]
    public List<string> SearchedKinds { get; set; } = [];

    [BindProperty]
    public string? IndexerIds { get; set; }

    [BindProperty]
    public int SearchLimit { get; set; } = 100;

    [BindProperty]
    public int Priority { get; set; } = 1;

    [BindProperty]
    public bool Enabled { get; set; } = true;

    [BindProperty]
    public bool AutomaticSearch { get; set; } = true;

    [BindProperty]
    public bool InteractiveSearch { get; set; } = true;

    public bool IsNew => Id is null;

    public string? Error { get; private set; }

    /// <summary>The media types of the media modules this instance serves, in the order the form lists them.</summary>
    public IReadOnlyList<MediaAcquisitionKind> Kinds { get; private set; } = [];

    /// <summary>What the indexer's capabilities allow per media type, shown beside the fields that override it; empty for a new or never-read indexer.</summary>
    public IReadOnlyList<IndexerKindReadiness> Detected { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadFormContextAsync(cancellationToken);
        if (Id is not { } id)
        {
            return;
        }

        var entry = await store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            Error = Ui["settings.indexers.notFound"];
            return;
        }

        Name = entry.Name;
        Type = entry.Type;
        BaseUrl = entry.Settings.BaseUrl;
        KindOverrides = Kinds.ToDictionary(AcquisitionAccessNames.Kind, kind => entry.Settings.CategoriesFor(kind) is { } chosen ? string.Join(", ", chosen) : null);
        SearchedKinds = [.. (entry.Settings.MediaKinds ?? []).Select(AcquisitionAccessNames.Kind)];
        IndexerIds = string.Join(", ", entry.Settings.IndexerIds);
        SearchLimit = entry.Settings.SearchLimit;
        Priority = entry.Priority;
        Enabled = entry.Enabled;
        AutomaticSearch = entry.Settings.AutomaticSearch;
        InteractiveSearch = entry.Settings.InteractiveSearch;
        Detected = [.. Kinds.Select(kind => IndexerReadiness.ForKind(entry, kind))];
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadFormContextAsync(cancellationToken);
        try
        {
            if (!TryParseIds(IndexerIds, out var indexerIds) || !TryParseKindOverrides(out var overrides))
            {
                Error = Ui["settings.indexers.invalidIds"];
                return Page();
            }

            var existing = Id is { } id ? await store.GetAsync(id, cancellationToken) : null;
            if (existing is null && Type == IndexerType.Newznab)
            {
                return await AddNewznabAsync(indexerIds, overrides, cancellationToken);
            }

            var apiKey = string.IsNullOrWhiteSpace(ApiKey) ? existing?.ApiKey : ApiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Error = Ui["settings.indexers.enterApiKey"];
                return Page();
            }

            var baseUrl = BaseUrl;
            if ((existing?.Type ?? Type) == IndexerType.Newznab && !IndexerSetupService.TryNormalizeBaseUrl(BaseUrl, out baseUrl, out var problem))
            {
                Error = problem;
                return Page();
            }

            await store.SaveAsync(Apply(existing ?? Create(baseUrl), indexerIds, overrides, apiKey, baseUrl), cancellationToken);
            TempData["IndexerNotice"] = Ui["settings.indexers.saved"];
            return RedirectToPage("/Admin/Usenet");
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArgumentOutOfRangeException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Saving indexer {Name} failed", Name);
            Error = Ui["settings.indexers.saveFailed"];
            return Page();
        }
    }

    private async Task<IActionResult> AddNewznabAsync(int[] indexerIds, Dictionary<string, int[]>? overrides, CancellationToken cancellationToken)
    {
        var result = await setup.AddAsync(BaseUrl, ApiKey, Name, cancellationToken);
        if (result.Outcome != IndexerSetupOutcome.Added || result.Entry is not { } added)
        {
            Error = IndexerSetupMessages.Describe(Ui, result).Text;
            return Page();
        }

        // What the owner set under Advanced applies on top of what setup detected; a search that was not proven keeps the indexer off whatever the checkbox says.
        await store.SaveAsync(Apply(added, indexerIds, overrides, added.ApiKey, added.Settings.BaseUrl) with { Enabled = Enabled && added.Enabled }, cancellationToken);
        var (text, isError) = IndexerSetupMessages.Describe(Ui, result);
        TempData[isError ? "UsenetError" : "UsenetNotice"] = text;
        return RedirectToPage("/Admin/Usenet");
    }

    private IndexerEntry Create(string baseUrl) => new(Guid.NewGuid(), Name, Type, Enabled, Priority, IndexerSettings.CreateDefault(baseUrl, Type), ApiKey ?? string.Empty);

    // What the form does not show (reported capabilities and their proof, the legacy anime and book category fields, settings of media types this
    // instance does not serve) stays exactly as stored. A new address invalidates what was read from the old one.
    private IndexerEntry Apply(IndexerEntry entry, int[] indexerIds, Dictionary<string, int[]>? overrides, string apiKey, string baseUrl)
    {
        var served = Kinds.Select(AcquisitionAccessNames.Kind).ToHashSet(StringComparer.Ordinal);
        var kept = (entry.Settings.CategoriesByKind ?? []).Where(pair => !served.Contains(pair.Key)).Concat(overrides ?? []).ToDictionary(pair => pair.Key, pair => pair.Value);
        var keptKinds = (entry.Settings.MediaKinds ?? []).Where(kind => !Kinds.Contains(kind));
        var searched = Kinds.Where(kind => SearchedKinds.Contains(AcquisitionAccessNames.Kind(kind))).Concat(keptKinds).ToArray();
        var moved = !string.Equals(entry.Settings.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase);
        return entry with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? entry.Name : Name,
            Enabled = Enabled,
            Priority = Priority,
            Settings = entry.Settings with
            {
                BaseUrl = baseUrl,
                IndexerIds = indexerIds,
                SearchLimit = SearchLimit,
                CategoriesByKind = kept.Count == 0 ? null : kept,
                MediaKinds = searched.Length == 0 ? null : searched,
                AutomaticSearch = AutomaticSearch,
                InteractiveSearch = InteractiveSearch,
                Capabilities = moved ? null : entry.Settings.Capabilities,
                Verification = moved ? null : entry.Settings.Verification
            },
            ApiKey = apiKey
        };
    }

    private async Task LoadFormContextAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var served = instanceModules is null ? InstanceModuleSettings.Default : await instanceModules.GetAsync(cancellationToken);
        Kinds = [.. UsenetModel.DownloadKinds.Where(kind => served.IsEnabled(AcquisitionInstanceModules.For(kind)))];
    }

    /// <summary>Reads the per-media-type fields; a field that is not a list of positive numbers refuses the whole form.</summary>
    private bool TryParseKindOverrides(out Dictionary<string, int[]>? overrides)
    {
        overrides = null;
        var parsed = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (name, text) in KindOverrides)
        {
            if (!TryParseIds(text, out var ids))
            {
                return false;
            }

            if (ids.Length > 0)
            {
                parsed[name.Trim().ToLowerInvariant()] = ids;
            }
        }

        overrides = parsed.Count == 0 ? null : parsed;
        return true;
    }

    private static bool TryParseIds(string? value, out int[] ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parsed = new List<int>();
        foreach (var part in value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var parsedId) || parsedId <= 0)
            {
                return false;
            }

            parsed.Add(parsedId);
        }

        ids = [.. parsed];
        return true;
    }
}
