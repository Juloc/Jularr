using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Acquisition;

/// <summary>
/// Owner overview of anime acquisition: schedule, monitored anime, wanted episodes, active
/// downloads, imports that need a decision and recent decisions with their reasons. Interactive
/// search results are shown on the same page so a grab stays one step away.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class IndexModel(
    AnimeAcquisitionPipeline pipeline,
    AnimeManualGrabService manualGrab,
    AnimeAcquisitionScheduler scheduler,
    AnimeImportExecutor importExecutor,
    DownloadClientStore downloadClients,
    AcquisitionHistoryService history,
    AppDbContext db,
    ILogger<IndexModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Episode { get; set; }

    [BindProperty(SupportsGet = true)]
    public ProwlarrAnimeSearchMode Mode { get; set; } = ProwlarrAnimeSearchMode.Episode;

    public AnimeAcquisitionOverview Overview { get; private set; } = null!;
    public AnimeInteractiveSearch? SearchResult { get; private set; }
    public bool ProwlarrConfigured { get; private set; }
    public bool SabnzbdConfigured { get; private set; }
    public string? ConfigurationError { get; private set; }
    public IReadOnlyList<AcquisitionHistoryEntry> RecentHistory { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> AnimeTitles { get; private set; } = new Dictionary<Guid, string>();
    public AnimeAcquisitionScheduler Scheduler => scheduler;
    public string? Notice => TempData["AcquisitionNotice"] as string;
    public string? Error => TempData["AcquisitionError"] as string;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        ProwlarrConfigured = await pipeline.IsProwlarrConfiguredAsync(cancellationToken);
        SabnzbdConfigured = (await downloadClients.LoadAllAsync(cancellationToken))
            .Any(entry => entry.Enabled && entry.Type == DownloadClientType.Sabnzbd);
        Overview = await pipeline.GetOverviewAsync(cancellationToken);
        RecentHistory = await history.RecentAsync(30, cancellationToken);
        if (RecentHistory.Count > 0)
        {
            var animeIds = RecentHistory.Select(entry => entry.AnimeId).Distinct().ToArray();
            AnimeTitles = await db.Anime
                .AsNoTracking()
                .Where(item => animeIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            SearchResult = await pipeline.SearchInteractiveAsync(
                Search.Trim(),
                Mode == ProwlarrAnimeSearchMode.Anime ? null : Season,
                Mode == ProwlarrAnimeSearchMode.Episode ? Episode : null,
                Mode,
                cancellationToken);
            if (SearchResult is null)
            {
                ConfigurationError = Ui["acquisition.error.animeNoLongerExists"];
            }
        }
    }

    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        SetQueued(scheduler.RequestRun(), Ui["acquisition.status.runAllQueued"]);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSearchAnimeAsync(
        string animeKey,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(animeKey))
        {
            return BadRequest();
        }

        SetQueued(scheduler.RequestRun(animeKey.Trim()), Ui["acquisition.status.searchQueued"]);
        return RedirectBack(returnUrl);
    }

    private void SetQueued(bool queued, string notice)
    {
        if (queued)
        {
            TempData["AcquisitionNotice"] = notice;
        }
        else
        {
            TempData["AcquisitionError"] = Ui["acquisition.error.tooManyQueued"];
        }
    }

    public async Task<IActionResult> OnPostAnimeSettingsAsync(
        Guid animeId,
        bool monitored,
        bool searchOnAdd,
        string? profileId,
        string? indexerIds,
        Guid? targetRootId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!TryParseIds(indexerIds, out var ids))
        {
            TempData["AcquisitionError"] = Ui["acquisition.error.indexerIdsFormat"];
            return RedirectBack(returnUrl);
        }

        try
        {
            var update = await pipeline.UpdateAnimeSettingsAsync(
                animeId,
                monitored,
                searchOnAdd,
                string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
                ids,
                cancellationToken,
                targetRootId);
            if (update is null)
            {
                return NotFound();
            }

            var queued = update.StartedMonitoring && searchOnAdd &&
                         scheduler.RequestRun(update.AnimeKey, AnimeSearchTrigger.SearchOnAdd);
            TempData["AcquisitionNotice"] = queued
                ? Ui["acquisition.status.settingsSavedSearching"]
                : Ui["acquisition.status.settingsSaved"];
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Anime acquisition settings update failed for {AnimeId}", animeId);
            TempData["AcquisitionError"] = Ui["acquisition.error.settingsUpdateFailed"];
        }

        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostGrabAsync(
        string animeKey,
        int? season,
        int? episode,
        ProwlarrAnimeSearchMode mode,
        string releaseIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animeKey) || string.IsNullOrWhiteSpace(releaseIdentity))
        {
            return BadRequest();
        }

        var result = await manualGrab.GrabAsync(animeKey, season, episode, mode, releaseIdentity, cancellationToken);
        TempData[result.Success ? "AcquisitionNotice" : "AcquisitionError"] = result.Message;
        return result.Success
            ? RedirectToPage()
            : RedirectToPage(new { search = animeKey, season, episode, mode });
    }

    public async Task<IActionResult> OnPostManualImportAsync(
        Guid recordId,
        string sourcePath,
        int season,
        int episode,
        CancellationToken cancellationToken)
    {
        var result = await importExecutor.ImportManuallyAsync(recordId, sourcePath, season, episode, cancellationToken);
        if (result.Success)
        {
            await scheduler.RunExclusiveAsync(
                async (runner, token) =>
                {
                    await runner.ReconcileAttemptsAsync(token);
                    return true;
                },
                cancellationToken);
        }

        TempData[result.Success ? "AcquisitionNotice" : "AcquisitionError"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDismissImportAsync(
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var result = await importExecutor.DismissAsync(recordId, cancellationToken);
        TempData[result.Success ? "AcquisitionNotice" : "AcquisitionError"] = result.Message;
        return RedirectToPage();
    }

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();

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
            if (!int.TryParse(part, out var id) || id <= 0)
            {
                return false;
            }

            parsed.Add(id);
        }

        ids = [.. parsed.Distinct().Order()];
        return true;
    }
}
