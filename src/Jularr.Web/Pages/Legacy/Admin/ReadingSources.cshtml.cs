using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.ReadingSources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public sealed record ReadingSourceRow(
    ReadingSourceDefinition Definition,
    ReadingSourcePreference Preference,
    ReadingSourceHealthSnapshot Health);

[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class ReadingSourcesModel(
    AppDbContext db,
    ReadingSourceSettingsStore store,
    ReadingSourceHealthTracker health,
    ILogger<ReadingSourcesModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<ReadingSourceRow> Sources { get; private set; } = [];
    public string? LoadError { get; private set; }

    public async Task OnGetAsync(
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(
            HttpContext,
            db);

        var settings = await LoadSettingsAsync(
            cancellationToken);

        Sources = Rows(definition => settings.Get(definition.Key));
    }

    public async Task<IActionResult> OnPostSaveAsync(
        List<string>? enabledProviders,
        Dictionary<string, int>? priority,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(
            HttpContext,
            db);
        var enabled = new HashSet<string>(
            enabledProviders ?? [],
            StringComparer.OrdinalIgnoreCase);
        var preferences =
            new Dictionary<string, ReadingSourcePreference>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in ReadingSourceCatalog.Definitions)
        {
            var sourcePriority =
                priority is not null &&
                priority.TryGetValue(
                    definition.Key,
                    out var configuredPriority)
                    ? configuredPriority
                    : definition.DefaultPriority;

            if (sourcePriority is < 1 or > 999)
            {
                ModelState.AddModelError(
                    definition.Key,
                    Ui.Format(
                        "admin.readingSources.priorityRange",
                        ("source", definition.Name),
                        ("min", 1),
                        ("max", 999)));
                continue;
            }

            preferences[definition.Key] =
                new ReadingSourcePreference(
                    enabled.Contains(definition.Key),
                    sourcePriority);
        }

        if (!ModelState.IsValid)
        {
            Sources = Rows(definition =>
                preferences.TryGetValue(
                    definition.Key,
                    out var preference)
                    ? preference
                    : ReadingSourceCatalog.DefaultPreference(definition));

            return Page();
        }

        await store.SaveAsync(
            new ReadingSourceSettingsState(preferences),
            cancellationToken);

        TempData["Status"] = Ui["admin.readingSources.saved"];
        return RedirectToPage();
    }

    private ReadingSourceRow[] Rows(
        Func<ReadingSourceDefinition, ReadingSourcePreference> preferenceFor) =>
        ReadingSourceCatalog.Definitions
            .Select(definition =>
                new ReadingSourceRow(
                    definition,
                    preferenceFor(definition),
                    health.Get(definition.Key)))
            .OrderBy(row => row.Preference.Priority)
            .ThenBy(row => row.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<ReadingSourceSettingsState> LoadSettingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.LoadAsync(
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            logger.LogError(exception, "Loading reading source settings failed");
            LoadError = Ui["admin.readingSources.loadFailed"];
            return ReadingSourceSettingsState.Default;
        }
    }
}
