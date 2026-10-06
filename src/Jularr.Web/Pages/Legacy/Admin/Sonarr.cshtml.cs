using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Jularr.Web.Features.Sonarr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SonarrModel(
    AppDbContext db,
    SonarrConnectionStore connectionStore,
    SonarrArtworkImportService sonarrService,
    BackgroundJobQueue jobs,
    CurrentAccountContext account) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty]
    public string BaseUrl { get; set; } = "http://sonarr:8989";

    [BindProperty]
    public string? ApiKey { get; set; }

    public bool HasSavedApiKey { get; private set; }
    public string? Notice => TempData["SonarrNotice"] as string;
    public string? Error => TempData["SonarrError"] as string;
    public SonarrArtworkImportResult? LastImport =>
        TempData.TryGetValue("SonarrImportResult", out var raw) && raw is string json
            ? System.Text.Json.JsonSerializer.Deserialize<SonarrArtworkImportResult>(json)
            : null;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var saved = await connectionStore.LoadAsync(cancellationToken);
        if (saved is not null)
        {
            BaseUrl = saved.BaseUrl;
            HasSavedApiKey = true;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var settings = await ResolveSubmittedSettingsAsync(cancellationToken);
        if (settings is null)
        {
            return RedirectToPage();
        }

        await connectionStore.SaveAsync(settings, cancellationToken);
        TempData["SonarrNotice"] = Ui["admin.sonarr.connectionSaved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var settings = await ResolveSubmittedSettingsAsync(cancellationToken);
        if (settings is null)
        {
            return RedirectToPage();
        }

        var result = await sonarrService.TestAsync(settings, cancellationToken);

        if (result.Success)
        {
            await connectionStore.SaveAsync(settings, cancellationToken);
            TempData["SonarrNotice"] = result.Message;
        }
        else
        {
            TempData["SonarrError"] = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var settings = await ResolveSubmittedSettingsAsync(cancellationToken);
        if (settings is null)
        {
            return RedirectToPage();
        }

        var test = await sonarrService.TestAsync(settings, cancellationToken);
        if (!test.Success)
        {
            TempData["SonarrError"] = test.Message;
            return RedirectToPage();
        }

        await connectionStore.SaveAsync(settings, cancellationToken);

        await jobs.QueueAsync(
            new OperationDescriptor(
                "sonarr-artwork-import",
                "Artwork",
                "Import Sonarr artwork",
                "Posters and fanart",
                account.ProfileId,
                OperationLane.Maintenance,
                IsDownload: true,
                Retryable: true),
            async (operation, services, workerToken) =>
            {
                await operation.ReportAsync(
                    5,
                    "Loading Sonarr library.",
                    cancellationToken: workerToken);

                var service =
                    services.GetRequiredService<SonarrArtworkImportService>();
                var result = await service.ImportAsync(
                    settings,
                    workerToken);

                await operation.ReportAsync(
                    100,
                    $"Imported {result.PosterCount} posters and {result.FanartCount} fanart images; {result.UnmatchedCount} unmatched.",
                    cancellationToken: workerToken);
            },
            cancellationToken);

        TempData["SonarrNotice"] = Ui["admin.sonarr.importQueued"];
        return RedirectToPage();
    }

    private async Task<SonarrConnectionSettings?> ResolveSubmittedSettingsAsync(
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var baseUrl = SonarrConnectionStore.NormalizeBaseUrl(BaseUrl);
        var apiKey = ApiKey?.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var saved = await connectionStore.LoadAsync(cancellationToken);
            apiKey = saved?.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            TempData["SonarrError"] = Ui["admin.sonarr.urlRequired"];
            return null;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            TempData["SonarrError"] = Ui["admin.sonarr.apiKeyRequired"];
            return null;
        }

        return new SonarrConnectionSettings(baseUrl, apiKey);
    }
}
