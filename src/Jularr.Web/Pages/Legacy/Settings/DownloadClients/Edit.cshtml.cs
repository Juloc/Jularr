using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings.DownloadClients;

/// <summary>Add or edit one canonical SABnzbd download client entry.</summary>
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class EditModel(
    AppDbContext db,
    DownloadClientStore store,
    ILogger<EditModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string BaseUrl { get; set; } = "";

    [BindProperty]
    public string? Secret { get; set; }

    [BindProperty]
    public string? BooksCategory { get; set; }

    [BindProperty]
    public string? AnimeCategory { get; set; }

    [BindProperty]
    public string? TvCategory { get; set; }

    [BindProperty]
    public string? MovieCategory { get; set; }

    [BindProperty]
    public string? MangaCategory { get; set; }

    [BindProperty]
    public string? LightNovelCategory { get; set; }

    [BindProperty]
    public string? MusicCategory { get; set; }

    [BindProperty]
    public string? AudiobookCategory { get; set; }

    [BindProperty]
    public int Priority { get; set; } = 1;

    [BindProperty]
    public bool Enabled { get; set; } = true;

    public bool IsNew => Id is null;
    public string? Error { get; private set; }
    public InstanceModuleSettings InstanceModules { get; private set; } = InstanceModuleSettings.Default;
    public bool ShowBooks => InstanceModules.IsEnabled(InstanceModule.Book);
    public bool ShowAnime => InstanceModules.IsEnabled(InstanceModule.Anime);
    public bool ShowTv => InstanceModules.IsEnabled(InstanceModule.Tv);
    public bool ShowMovie => InstanceModules.IsEnabled(InstanceModule.Movie);
    public bool ShowManga => InstanceModules.IsEnabled(InstanceModule.Manga);
    public bool ShowLightNovels => InstanceModules.IsEnabled(InstanceModule.Novel);
    public bool ShowMusic => InstanceModules.IsEnabled(InstanceModule.Music);
    public bool ShowAudiobooks => InstanceModules.IsEnabled(InstanceModule.Audiobook);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadInstanceModulesAsync(cancellationToken);
        if (Id is not { } id)
        {
            return;
        }

        var entry = await store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            Error = Ui["settings.downloadClients.notFound"];
            return;
        }

        Name = entry.Name;
        BaseUrl = entry.Settings.BaseUrl;
        BooksCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Book);
        AnimeCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Anime);
        TvCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Tv);
        MovieCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Movie);
        MangaCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Manga);
        LightNovelCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.LightNovel);
        MusicCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Music);
        AudiobookCategory = entry.Settings.CategoryFor(MediaAcquisitionKind.Audiobook);
        Priority = entry.Priority;
        Enabled = entry.Enabled;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadInstanceModulesAsync(cancellationToken);
        try
        {
            var existing = Id is { } id ? await store.GetAsync(id, cancellationToken) : null;
            var secret = string.IsNullOrWhiteSpace(Secret) ? existing?.Secret : Secret.Trim();
            if (string.IsNullOrWhiteSpace(secret))
            {
                Error = Ui["settings.downloadClients.enterApiKey"];
                return Page();
            }

            await store.SaveAsync(
                new DownloadClientEntry(
                    existing?.Id ?? Guid.NewGuid(),
                    Name,
                    DownloadClientType.Sabnzbd,
                    Enabled,
                    Priority,
                    new DownloadClientSettings(
                        BaseUrl,
                        new Dictionary<MediaAcquisitionKind, string?>
                        {
                            [MediaAcquisitionKind.Anime] = ShowAnime
                                ? AnimeCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Anime),
                            [MediaAcquisitionKind.Tv] = ShowTv
                                ? TvCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Tv),
                            [MediaAcquisitionKind.Movie] = ShowMovie
                                ? MovieCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Movie),
                            [MediaAcquisitionKind.Manga] = ShowManga
                                ? MangaCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Manga),
                            [MediaAcquisitionKind.LightNovel] = ShowLightNovels
                                ? LightNovelCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.LightNovel),
                            [MediaAcquisitionKind.Book] = ShowBooks
                                ? BooksCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Book),
                            [MediaAcquisitionKind.Music] = ShowMusic
                                ? MusicCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Music),
                            [MediaAcquisitionKind.Audiobook] = ShowAudiobooks
                                ? AudiobookCategory
                                : existing?.Settings.CategoryFor(MediaAcquisitionKind.Audiobook)
                        }),
                    secret),
                cancellationToken);

            TempData["DownloadClientNotice"] = Ui["settings.downloadClients.saved"];
            return RedirectToPage("/Admin/Usenet");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Saving download client {Name} failed", Name);
            Error = Ui["settings.downloadClients.saveFailed"];
            return Page();
        }
    }
    private async Task LoadInstanceModulesAsync(CancellationToken cancellationToken)
    {
        InstanceModules = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
    }
}
