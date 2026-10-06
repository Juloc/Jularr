using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class SubtitlesModel(
    AppDbContext db,
    SubtitleImportService subtitleImportService,
    SubtitleCompletenessService completenessService,
    SubtitleManualSearchService manualSearchService) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public LearningTextCoverageSnapshot Coverage { get; private set; } =
        new(0, 0, 0, 0, 0, 0);

    public string ContentLanguageName { get; private set; } =
        LanguageName(LearningContentLanguageResolver.DefaultLanguage);

    public JimakuConnectionStatus Jimaku { get; private set; } =
        new(false);

    public IReadOnlyList<LearningTextEpisodeStatus> MissingEpisodes { get; private set; } = [];

    /// <summary>Per-episode language-profile completeness (#526): complete / cutoff met / missing X.</summary>
    public IReadOnlyList<SubtitleEpisodeCompletion> Completions { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public Guid? SearchEpisodeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? SearchLanguage { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool SearchForced { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool SearchSdh { get; set; }

    public SubtitleEpisodeCompletion? SearchEpisode { get; private set; }
    public IReadOnlyList<SubtitleManualSearchOutcome> SearchOutcomes { get; private set; } = [];
    // Every configured provider yields an outcome (results or an error), so none means none configured.
    public bool HasProviders => SearchOutcomes.Count > 0;

    [BindProperty]
    public string JimakuApiKey { get; set; } = "";

    public string? ErrorMessage => TempData["Error"] as string;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveJimakuAsync(
        CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        var result = await subtitleImportService.SaveJimakuApiKeyAsync(
            JimakuApiKey,
            cancellationToken);

        TempData[result.Success ? "Status" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisconnectJimakuAsync(
        CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await subtitleImportService.DisconnectJimakuAsync(cancellationToken);
        TempData["Status"] = Ui["admin.subtitles.jimakuRemoved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPrepareAllAsync(
        CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var count = await subtitleImportService.QueueAllMissingAsync(
            cancellationToken);

        TempData["Status"] = count == 0
            ? Ui["admin.subtitles.noneQueued"]
            : Ui.Format("admin.subtitles.queuedCount", ("count", count));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var queued = await subtitleImportService.QueueLearningTextAsync(
            episodeId,
            cancellationToken);

        TempData["Status"] = queued
            ? Ui["admin.subtitles.preparationQueued"]
            : Ui["admin.subtitles.episodeAlreadyReady"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportSearchResultAsync(
        Guid episodeId,
        string providerId,
        string resultToken,
        string language,
        bool forced,
        bool sdh,
        string releaseName,
        string? uploader,
        CancellationToken cancellationToken)
    {
        if (!JularrPolicies.Allows(User, JularrPolicies.AdminMedia))
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var result = new SubtitleSearchResult(
            providerId, resultToken, language, forced, sdh, releaseName, uploader, null, null);
        var downloaded = await manualSearchService.ImportAsync(episodeId, result, cancellationToken);

        TempData[downloaded.Success ? "Status" : "Error"] = downloaded.Success
            ? Ui["admin.subtitles.search.imported"]
            : downloaded.Message ?? Ui["admin.subtitles.search.importFailed"];

        return RedirectToPage(new
        {
            searchEpisodeId = episodeId,
            searchLanguage = language,
            searchForced = forced,
            searchSdh = sdh
        });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Coverage = await subtitleImportService.GetCoverageAsync(cancellationToken);
        ContentLanguageName = LanguageName(
            await new LearningContentLanguageResolver(db)
                .ResolveTargetLanguageAsync(cancellationToken));
        Jimaku = await subtitleImportService.GetJimakuConnectionStatusAsync(
            cancellationToken);
        MissingEpisodes = await subtitleImportService.GetMissingEpisodesAsync(
            200,
            cancellationToken);
        Completions = await completenessService.GetCompletionsAsync(200, cancellationToken);

        if (SearchEpisodeId is Guid episodeId && !string.IsNullOrWhiteSpace(SearchLanguage))
        {
            SearchEpisode = await completenessService.GetEpisodeCompletionAsync(episodeId, cancellationToken);
            if (SearchEpisode is not null)
            {
                var request = new SubtitleSearchRequest(
                    episodeId,
                    SearchEpisode.AnimeTitle,
                    SearchEpisode.SeasonNumber,
                    SearchEpisode.EpisodeNumber,
                    SearchLanguage,
                    SearchForced,
                    SearchSdh);
                SearchOutcomes = await manualSearchService.SearchAsync(request, cancellationToken);
            }
        }
    }

    private static string LanguageName(string languageTag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageTag).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return languageTag;
        }
    }
}
