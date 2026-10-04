using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Recommendations;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Discover;

public sealed class IndexModel(
    IDiscoveryFeed coordinator,
    DiscoveryShelfService shelves,
    TmdbDiscoveryProvider tmdb,
    AppDbContext db,
    NovelImportService novels,
    NovelMetadataService novelMetadata,
    CurrentAccountContext account,
    OperationRunner operations,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    WatchlistStore watchlist,
    FranchiseService franchiseService,
    MediaRecommendationService recommendations,
    ILogger<IndexModel> logger,
    IAppShellService? shell = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public bool IsOwner => account.IsOwner;

    /// <summary>What the address asks for; the address is the only state of the page.</summary>
    public DiscoverBrowseQuery Query { get; private set; } = new();

    /// <summary>The preferred audio and subtitle language of the profile, offered as a filter when set.</summary>
    public LibraryLanguagePreference Preference { get; private set; } = LibraryLanguagePreference.None;

    /// <summary>The card action per AniList category: "add", "request" or "" (none).</summary>
    public IReadOnlyDictionary<string, string> AddActions { get; private set; } = new Dictionary<string, string>();

    /// <summary>The page itself reads local state only (#186); the titles come from <see cref="OnGetBodyAsync"/> after first paint.</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = ParseQuery();
        AddActions = await LoadAddActionsAsync(cancellationToken);
        Preference = await LoadPreferenceAsync(cancellationToken);
    }

    // Discover has one acquisition action for every supported media kind. Capability policy decides
    // whether the button means Request or Add; media-specific executors decide how acquisition happens.
    public static string AddAction(MediaAcquisitionKind kind, AcquisitionCapabilities access) =>
        !access.CanAdd
            ? ""
            : access.AddCreatesRequest
                ? "request"
                : "add";

    private DiscoverBrowseQuery ParseQuery() =>
        DiscoverBrowseQuery.Parse(key => Request.Query.TryGetValue(key, out var values) ? values.ToString() : null);

    private async Task<IReadOnlyDictionary<string, string>> LoadAddActionsAsync(CancellationToken cancellationToken)
    {
        var actions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (category, kind) in Categories)
        {
            actions[category] = AddAction(kind, await requests.GetCapabilitiesAsync(kind, cancellationToken));
        }

        return actions;
    }

    private async Task<LibraryLanguagePreference> LoadPreferenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await db.ProfilePlaybackPreferences
                .AsNoTracking()
                .Where(x => x.ProfileId == account.ProfileId)
                .Select(x => new { x.PreferredAudioLanguage, x.PreferredSubtitleLanguage })
                .SingleOrDefaultAsync(cancellationToken);
            return preferences is null
                ? LibraryLanguagePreference.None
                : LibraryLanguagePreference.From(preferences.PreferredAudioLanguage, preferences.PreferredSubtitleLanguage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the language indicator and its filter are lost.
            logger.LogWarning(exception, "The playback language preferences could not be read for Discover.");
            return LibraryLanguagePreference.None;
        }
    }

    /// <summary>
    /// The body of the page for one address (rows on the landing, one grid for a search or drill-down),
    /// rendered on the server and fetched from the client after first paint so the page GET stays local
    /// (#186). The provider board is TTL-cached by the shelf service and the feed.
    /// </summary>
    public async Task<IActionResult> OnGetBodyAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = ParseQuery();

        try
        {
            AddActions = await LoadAddActionsAsync(cancellationToken);
            Preference = await LoadPreferenceAsync(cancellationToken);
            var body = Query.IsLanding
                ? await BuildLandingAsync(cancellationToken)
                : await BuildResultsAsync(cancellationToken);
            return Partial("_DiscoverBody", body);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The Discover body could not be built.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Partial("_DiscoverBody", DiscoverBodyView.Of(Ui, Query, DiscoverBodyState.Unavailable));
        }
    }

    private async Task<DiscoverBodyView> BuildLandingAsync(CancellationToken cancellationToken)
    {
        var board = await shelves.GetBoardAsync(
            User,
            account.ProfileId,
            account.IsOwner,
            includeAniList: true,
            includeBooks: true,
            cancellationToken);

        var rows = new List<(string Id, string Heading, string? SeeAll, IReadOnlyList<DiscoveryItem> Items)>();

        // Personalized cross-media rows (#428) lead the board: explainable "Because you …" and
        // continuation shelves for this profile. They enrich the page, so a failure only drops them.
        try
        {
            var personalized = await recommendations.GetForProfileAsync(User, account.ProfileId, cancellationToken);
            foreach (var shelf in personalized.Shelves)
            {
                var items = shelf.Items
                    .Where(item => DiscoverScopes.Includes(
                        Query.Category,
                        DiscoverRecommendations.CategoryOf(item.Candidate.MediaType)))
                    .Select(item => DiscoverRecommendations.ToItem(item.Candidate))
                    .ToArray();
                if (items.Length > 0)
                {
                    rows.Add((shelf.Id, RecommendationHeading(shelf), null, items));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The personalized rows could not be loaded for Discover.");
        }

        var providerRows = Query.Category is DiscoveryCategory.All or DiscoveryCategory.BooksAndLightNovels
            ? DiscoveryShelfComposer.CombineBooksAndLightNovels(board.Rows)
            : board.Rows;

        foreach (var row in providerRows.Where(row => DiscoverScopes.Includes(Query.Category, row.Category)))
        {
            rows.Add((row.Id, ShelfHeading(row), row.DeepLinkUrl, row.Items));
        }

        var collapsed = rows
            .Select(row => (row.Id, row.Heading, row.SeeAll, Items: DiscoverCanonical.Collapse(row.Items)))
            .Where(row => row.Items.Count > 0)
            .ToArray();

        var context = await BuildContextAsync(collapsed.SelectMany(row => row.Items), cancellationToken);
        var views = collapsed
            .Select(row => new DiscoverShelfView(
                row.Id,
                row.Heading,
                row.SeeAll,
                [.. row.Items.Select(item => DiscoverCardFactory.Create(item, context))]))
            .ToArray();

        var degraded = board.Warnings.Count > 0;
        return new DiscoverBodyView(
            Ui,
            Query,
            views.Length > 0
                ? DiscoverBodyState.Shelves
                : degraded ? DiscoverBodyState.Unavailable : DiscoverBodyState.Empty,
            views,
            [],
            views.Sum(shelf => shelf.Cards.Count),
            degraded && views.Length > 0);
    }

    private async Task<DiscoverBodyView> BuildResultsAsync(CancellationToken cancellationToken)
    {
        if (!Query.IsSearch && Query.Mode == DiscoveryMode.MyList && Query.Category == DiscoveryCategory.Book)
        {
            return DiscoverBodyView.Of(Ui, Query, DiscoverBodyState.BooksNotInList);
        }

        var response = await coordinator.GetAsync(
            Query.ToRequest(),
            account.ProfileId,
            account.IsOwner,
            includeAniList: true,
            includeBooks: true,
            cancellationToken);

        if (!Query.IsSearch && Query.Mode == DiscoveryMode.MyList && !response.AniListConnected)
        {
            return DiscoverBodyView.Of(Ui, Query, DiscoverBodyState.NotConnected);
        }

        var items = DiscoverCanonical.Collapse(response.Items);
        var context = await BuildContextAsync(items, cancellationToken);
        var cards = items.Select(item => DiscoverCardFactory.Create(item, context)).ToArray();
        var shown = DiscoverFilter.Apply(cards, Query);
        var degraded = response.Warnings.Count > 0;

        var state = shown.Count > 0
            ? DiscoverBodyState.Results
            : cards.Length > 0
                ? DiscoverBodyState.NoResults
                : degraded ? DiscoverBodyState.Unavailable : DiscoverBodyState.Empty;

        return new DiscoverBodyView(Ui, Query, state, [], shown, cards.Length, degraded && shown.Count > 0);
    }

    private string RecommendationHeading(MediaRecommendationShelf shelf)
    {
        var title = shelf.SeedTitle ?? "";
        return shelf.Kind == MediaRecommendationShelfKind.Continuation
            ? Ui.Format("recommendations.shelf.continue", ("title", title))
            : Ui.Format("recommendations.shelf.becauseYou", ("title", title));
    }

    // With one media type picked the type is the scope, so the heading is only the ordering.
    private string ShelfHeading(DiscoveryShelfRow row) =>
        Query.Category == DiscoveryCategory.All && row.MediaLabelKey is { Length: > 0 } mediaKey
            ? $"{Ui[row.TitleKey]} · {Ui[mediaKey]}"
            : Ui[row.TitleKey];

    /// <summary>
    /// Reads what the cards need beyond the titles: open requests, followed works and the languages of the
    /// library titles among them. Each is a supporting detail, so a failure only removes it from the cards.
    /// </summary>
    private async Task<DiscoverContext> BuildContextAsync(
        IEnumerable<DiscoveryItem> items,
        CancellationToken cancellationToken)
    {
        var list = items.ToArray();

        IReadOnlyDictionary<(MediaAcquisitionKind, string), AcquisitionRequest> open =
            new Dictionary<(MediaAcquisitionKind, string), AcquisitionRequest>();
        try
        {
            open = (await requestStore.ListAsync(null, null, openOnly: true, limit: 500, cancellationToken))
                .GroupBy(item => (item.Kind, item.ExternalId))
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.UpdatedAt).First());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The open requests could not be read for Discover.");
        }

        IReadOnlyDictionary<string, Guid?> followed = new Dictionary<string, Guid?>();
        try
        {
            followed = (await watchlist.GetEffectiveAsync(account.ProfileId, cancellationToken))
                .ToDictionary(item => item.Identity.Key, item => item.FranchiseId, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The watchlist could not be read for Discover.");
        }

        return new DiscoverContext(
            Ui,
            Preference,
            open,
            await LoadLocalFactsAsync(list, cancellationToken),
            followed,
            AddActions);
    }

    private async Task<IReadOnlyDictionary<string, DiscoverLocalFacts>> LoadLocalFactsAsync(
        IReadOnlyList<DiscoveryItem> items,
        CancellationToken cancellationToken)
    {
        if (!items.Any(item => item.IsLocal && item.Category == "anime"))
        {
            return new Dictionary<string, DiscoverLocalFacts>();
        }

        try
        {
            var entries = await new LibraryMediaCardQuery(db).GetAnimeEntriesAsync(account.ProfileId, cancellationToken);
            return entries.Entries
                .GroupBy(entry => entry.Card.Href, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var card = group.First().Card;
                        var action = MediaBannerCardModel.Create(card, Ui).Action;
                        return new DiscoverLocalFacts(
                            card.AudioLanguages ?? [],
                            card.SubtitleLanguages ?? [],
                            action?.Url,
                            action?.Label);
                    },
                    StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The cards fall back to "In library" without a language.
            logger.LogWarning(exception, "The library languages could not be read for Discover.");
            return new Dictionary<string, DiscoverLocalFacts>();
        }
    }

    /// <summary>Follows or unfollows one work for this profile. Library state is never taken from the browser.</summary>
    public async Task<IActionResult> OnPostWatchlistAsync(
        string? category,
        string? provider,
        string? externalId,
        string? title,
        string? nativeTitle,
        string? coverImageUrl,
        string? format,
        string? status,
        int? year,
        bool follow,
        CancellationToken cancellationToken)
    {
        if (!WatchlistDraftInput.TryCreate(
                category,
                provider,
                externalId,
                title,
                nativeTitle,
                coverImageUrl,
                format,
                status,
                year,
                out var draft))
        {
            return BadRequest();
        }

        if (!await IsVisibleAsync(draft.Identity, cancellationToken))
        {
            return NotFound();
        }

        if (follow)
        {
            await watchlist.FollowAsync(account.ProfileId, draft, cancellationToken);
        }
        else
        {
            await watchlist.UnfollowAsync(account.ProfileId, draft.Identity, cancellationToken);
        }

        return new JsonResult(new { followed = follow });
    }

    /// <summary>
    /// Follows the franchise of an AniList title. Only the identity is taken from the browser;
    /// the franchise's works are read from AniList in the background.
    /// </summary>
    public async Task<IActionResult> OnPostFollowFranchiseAsync(
        string? category,
        string? provider,
        string? externalId,
        CancellationToken cancellationToken)
    {
        if (!WatchlistDraftInput.TryIdentity(category, provider, externalId, out var identity) ||
            !FranchiseService.CanSeed(identity))
        {
            return BadRequest();
        }

        if (!await IsVisibleAsync(identity, cancellationToken))
        {
            return NotFound();
        }

        var franchiseId = await franchiseService.FollowFromSeedAsync(
            account.ProfileId,
            identity,
            cancellationToken);
        return new JsonResult(new { followed = true, franchiseId, franchiseUrl = $"/Franchises/{franchiseId}" });
    }

    /// <summary>
    /// The one Discover add/request entry point. A profile submits the canonical provider identity shown
    /// on the card; after policy approval the registered media executor owns search, download and import.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(
        string? category,
        string? provider,
        string? externalId,
        string? title,
        string? subtitle,
        string? author,
        string? coverImageUrl,
        CancellationToken cancellationToken)
    {
        if (category is null
            || !Categories.TryGetValue(category, out var kind)
            || string.IsNullOrWhiteSpace(externalId)
            || string.IsNullOrWhiteSpace(title))
        {
            return BadRequest();
        }

        if (!await IsVisibleAsync(AcquisitionAccessNames.WorkType(kind), cancellationToken))
        {
            return NotFound();
        }

        var capabilities = await requests.GetCapabilitiesAsync(kind, cancellationToken);
        if (!capabilities.CanAdd)
        {
            return Forbid();
        }

        var canonicalProvider = kind switch
        {
            MediaAcquisitionKind.Book => Jularr.Web.Features.Books.BookCatalogService.CatalogRequestProvider,
            MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv => TmdbDiscoveryProvider.ProviderKey,
            _ => AniListMetadataProvider.ProviderKey
        };
        var canonicalExternalId = externalId.Trim();

        if (kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
        {
            if (!string.Equals(provider, TmdbDiscoveryProvider.ProviderKey, StringComparison.Ordinal)
                || !TmdbDiscoveryProvider.TryNormalizeExternalId(externalId, out canonicalExternalId))
            {
                return BadRequest();
            }

            try
            {
                await tmdb.EnsureCanonicalWorkAsync(
                    kind == MediaAcquisitionKind.Movie
                        ? TmdbDiscoveryMediaType.Movie
                        : TmdbDiscoveryMediaType.Series,
                    canonicalExternalId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException
                                               or InvalidOperationException
                                               or InvalidDataException)
            {
                logger.LogWarning(
                    exception,
                    "TMDB identity {Kind}/{ExternalId} could not be materialized before request.",
                    kind,
                    externalId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }
        else if (kind != MediaAcquisitionKind.Book)
        {
            if (!string.Equals(provider, AniListMetadataProvider.ProviderKey, StringComparison.Ordinal)
                || !int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId)
                || aniListId <= 0)
            {
                return BadRequest();
            }

            canonicalExternalId = aniListId.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            var request = await requests.SubmitAsync(
                new AcquisitionRequestDraft(
                    kind,
                    canonicalProvider,
                    canonicalExternalId,
                    title.Trim(),
                    kind == MediaAcquisitionKind.Book
                        ? string.IsNullOrWhiteSpace(author) ? null : author.Trim()
                        : string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
                    string.IsNullOrWhiteSpace(coverImageUrl) ? null : coverImageUrl.Trim()),
                cancellationToken);
            return new JsonResult(await RequestProgressAsync(request, cancellationToken));
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Live acquisition state for a title already visible in Discover. The payload deliberately contains
    /// no requester identity or approval metadata; requests are title-wide so every viewer sees the same
    /// download/import progress instead of being offered a duplicate request.
    /// </summary>
    public async Task<IActionResult> OnGetRequestStatusAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var request = await requestStore.GetAsync(id, cancellationToken);
        return request is null
            ? NotFound()
            : new JsonResult(await RequestProgressAsync(request, cancellationToken));
    }

    private async Task<object> RequestProgressAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        OperationSnapshot? operation = null;
        if (request.OperationId is { } operationId)
        {
            operation = await new OperationStore(db).GetAsync(operationId, cancellationToken);
        }

        var status = AcquisitionAccessNames.Status(request.Status);
        var percent = request.Status switch
        {
            AcquisitionRequestStatus.Pending => 0,
            AcquisitionRequestStatus.Approved => 5,
            AcquisitionRequestStatus.Searching => 15,
            AcquisitionRequestStatus.Downloading => operation?.ProgressPercent ?? 35,
            AcquisitionRequestStatus.Importing => operation?.ProgressPercent is { } importProgress
                ? Math.Max(80, importProgress)
                : 90,
            AcquisitionRequestStatus.Completed => 100,
            AcquisitionRequestStatus.Rejected => 100,
            AcquisitionRequestStatus.Failed => 100,
            _ => 0
        };

        return new
        {
            requestId = request.Id,
            status,
            progress = Math.Clamp(percent, 0, 100),
            message = request.StatusMessage ?? operation?.Message,
            resultUrl = request.ResultUrl,
            done = request.Status is AcquisitionRequestStatus.Completed
                or AcquisitionRequestStatus.Rejected
                or AcquisitionRequestStatus.Failed
        };
    }

    private static readonly IReadOnlyDictionary<string, MediaAcquisitionKind> Categories =
        new Dictionary<string, MediaAcquisitionKind>(StringComparer.Ordinal)
        {
            ["anime"] = MediaAcquisitionKind.Anime,
            ["manga"] = MediaAcquisitionKind.Manga,
            ["light-novel"] = MediaAcquisitionKind.LightNovel,
            ["book"] = MediaAcquisitionKind.Book,
            ["movie"] = MediaAcquisitionKind.Movie,
            ["tv"] = MediaAcquisitionKind.Tv
        };

    public async Task<IActionResult> OnPostImportSourceAsync(
        string sourceUrl,
        string? metadataProvider,
        string? metadataExternalId,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        if (!await IsVisibleAsync(WorkMediaType.LightNovel, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            var result = await operations.RunAsync(
                new OperationDescriptor(
                    "discover-novel-import",
                    "Novels",
                    "Import discovered novel",
                    string.IsNullOrWhiteSpace(metadataExternalId)
                        ? null
                        : $"AniList {metadataExternalId.Trim()}",
                    account.ProfileId,
                    OperationLane.Normal,
                    IsDownload: true,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        10,
                        "Importing novel source.",
                        cancellationToken: token);

                    var workId = await novels.ImportWorkAsync(
                        sourceUrl,
                        token);

                    if (!string.IsNullOrWhiteSpace(metadataProvider) &&
                        !string.IsNullOrWhiteSpace(metadataExternalId))
                    {
                        try
                        {
                            await operation.ReportAsync(
                                80,
                                "Matching imported novel metadata.",
                                cancellationToken: token);

                            await novelMetadata.MatchAsync(
                                workId,
                                metadataProvider,
                                metadataExternalId,
                                token);

                            return (WorkId: workId, Status: Ui["discover.import.novelMatched"]);
                        }
                        catch (Exception exception) when (
                            exception is InvalidOperationException or
                            NovelMetadataProviderException)
                        {
                            await operation.LogAsync(
                                OperationLogLevel.Warning,
                                "NovelMetadata",
                                "Novel import completed, but metadata matching needs attention.",
                                CancellationToken.None);

                            return (
                                WorkId: workId,
                                Status: Ui.Format(
                                    "discover.import.novelMatchAttention",
                                    ("reason", exception.Message)));
                        }
                    }

                    return (
                        WorkId: workId,
                        Status: Ui["discover.import.novelImported"]);
                },
                "Discovered novel imported.",
                cancellationToken);

            TempData["Status"] = result.Status;
            return RedirectToPage("/Novels/Work", new { id = result.WorkId });
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Importing discovered novel source {SourceUrl} failed", sourceUrl);
            TempData["Status"] = Ui["discover.import.sourceImportFailed"];
            return RedirectToPage();
        }
    }

    private async Task<bool> IsVisibleAsync(
        WatchlistIdentity identity,
        CancellationToken cancellationToken) =>
        await IsVisibleAsync(
            WorkMediaTypes.FromWatchlist(identity.MediaType),
            cancellationToken);

    private async Task<bool> IsVisibleAsync(
        WorkMediaType mediaType,
        CancellationToken cancellationToken)
    {
        if (shell is null)
        {
            return true;
        }

        var access = await shell.GetMediaAccessAsync(User, cancellationToken);
        return access.VisibleMediaTypes.Contains(mediaType);
    }
}
