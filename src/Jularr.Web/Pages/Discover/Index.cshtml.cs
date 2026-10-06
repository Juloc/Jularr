using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback;
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
    VideoRequestScopeResolver scopes,
    WatchlistStore watchlist,
    FranchiseService franchiseService,
    MediaRecommendationService recommendations,
    IInstanceModuleService modules,
    ILogger<IndexModel> logger,
    IAppShellService? shell = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public bool IsOwner => account.IsOwner;

    /// <summary>What the address asks for; the address is the only state of the page.</summary>
    public DiscoverBrowseQuery Query { get; private set; } = new();

    /// <summary>The preferred audio and subtitle language of the profile, offered as a filter when set.</summary>
    public LibraryLanguagePreference Preference { get; private set; } = LibraryLanguagePreference.None;

    /// <summary>The categories this profile may request. Request and Instant capabilities look the same here: auto-approval is policy, never another action.</summary>
    public IReadOnlySet<string> RequestableCategories { get; private set; } = new HashSet<string>();

    /// <summary>The page itself reads local state only (#186); the titles come from <see cref="OnGetBodyAsync"/> after first paint.</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = ParseQuery();
        RequestableCategories = await LoadRequestableCategoriesAsync(cancellationToken);
        Preference = await LoadPreferenceAsync(cancellationToken);
    }

    private DiscoverBrowseQuery ParseQuery() =>
        DiscoverBrowseQuery.Parse(key => Request.Query.TryGetValue(key, out var values) ? values.ToString() : null);

    private async Task<IReadOnlySet<string>> LoadRequestableCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (category, kind) in Categories)
        {
            if ((await requests.GetCapabilitiesAsync(kind, cancellationToken)).CanRequest)
            {
                categories.Add(category);
            }
        }

        return categories;
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
            RequestableCategories = await LoadRequestableCategoriesAsync(cancellationToken);
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
            RequestableCategories);
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
            var entries = await new LibraryMediaCardQuery(db).GetEntriesAsync(account.ProfileId, [WorkMediaType.Anime], cancellationToken);
            var playbackEnabled = await modules.IsEnabledAsync(InstanceModule.Playback, cancellationToken);
            return entries.Entries
                .GroupBy(entry => entry.Card.Href, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => DiscoverLocalFacts.From(group.First().Card, Ui, playbackEnabled),
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
    /// Opens the Request dialog for one card: the canonical identity is resolved first (a series gets its Work and
    /// season/episode structure), then only the settings groups that apply to the media kind are returned. Resolving
    /// can create the canonical Work, so it is a POST and never a GET.
    /// </summary>
    public async Task<IActionResult> OnPostResolveAsync(
        string? category,
        string? provider,
        string? externalId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var (target, refusal) = await ResolveRequestTargetAsync(category, provider, externalId, cancellationToken);
        if (target is null)
        {
            return refusal!;
        }

        var existing = await requestStore.FindOpenAsync(target.Kind, target.Provider, target.ExternalId, cancellationToken);
        var seasons = existing is null && target.Kind == MediaAcquisitionKind.Tv && target.Work is { } work
            ? await scopes.LoadStructureAsync(work.Id, cancellationToken)
            : [];
        var preference = await LoadPreferenceAsync(cancellationToken);
        return Partial("_DiscoverRequestSettings", new DiscoverRequestSettingsView(Ui, target.Kind, existing, seasons, OfferedLanguage(preference.Audio), OfferedLanguage(preference.Subtitle)));
    }

    /// <summary>
    /// The one Discover Request entry point, for every media kind and for Request and Instant capabilities alike. A
    /// profile submits the canonical provider identity shown on the card plus the dialog settings; scope and language
    /// are validated here against what the kind supports, after policy approval the registered media executor owns
    /// search, download and import.
    /// </summary>
    public async Task<IActionResult> OnPostRequestAsync([FromForm] DiscoverRequestForm form, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (string.IsNullOrWhiteSpace(form.Title))
        {
            return BadRequest();
        }

        var (target, refusal) = await ResolveRequestTargetAsync(form.Category, form.Provider, form.ExternalId, cancellationToken);
        if (target is null)
        {
            return refusal!;
        }

        var draft = new AcquisitionRequestDraft(
            target.Kind,
            target.Provider,
            target.ExternalId,
            form.Title.Trim(),
            target.Kind == MediaAcquisitionKind.Book ? Trimmed(form.Author) : Trimmed(form.Subtitle),
            Trimmed(form.CoverImageUrl));
        List<string> summary = [];

        try
        {
            if (target.Kind == MediaAcquisitionKind.Tv)
            {
                if (!VideoRequestScopeResolver.TryParseScope(form.Scope, out var scope) || target.Work is not { } work || form.HasLanguage)
                {
                    return BadRequest();
                }

                var payload = await scopes.BuildTvPayloadAsync(work, new VideoRequestScopeChoice(scope, form.SeasonIds, form.EpisodeIds, form.MonitorFuture), cancellationToken);
                draft = draft with { PayloadJson = payload.Serialize() };
                summary.Add(DiscoverRequestSummary.Scope(payload, Ui));
            }
            else if (target.Kind == MediaAcquisitionKind.Anime)
            {
                if (form.HasScope)
                {
                    return BadRequest();
                }

                var options = new AcquisitionRequestOptions { AudioLanguage = form.Audio, SubtitleLanguage = form.Subtitles }.Validate();
                draft = draft with { Options = options };
                summary.Add(Ui["discover.request.scope.all"]);
                summary.AddRange(RequestOptionsSummary.Describe(options, Ui));
            }
            else if (form.HasScope || form.HasLanguage)
            {
                return BadRequest();
            }

            var submission = await requests.SubmitWithOutcomeAsync(draft, cancellationToken);
            var progress = await RequestProgressAsync(submission.Request, cancellationToken);
            return Partial("_DiscoverRequestResult", new DiscoverRequestResultView(Ui, submission.Request, submission.AlreadyRequested, submission.AlreadyRequested ? [] : summary, progress));
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    /// <summary>
    /// Validates the card's identity on the server and resolves the canonical target a Request is about: the browser
    /// only names a category and a provider id; visibility, capability and the provider/id shape are checked here and
    /// Movie/TV identities are promoted to their canonical Work before any request exists.
    /// </summary>
    private async Task<(RequestTarget? Target, IActionResult? Refusal)> ResolveRequestTargetAsync(
        string? category,
        string? provider,
        string? externalId,
        CancellationToken cancellationToken)
    {
        if (category is null || !Categories.TryGetValue(category, out var kind) || string.IsNullOrWhiteSpace(externalId))
        {
            return (null, BadRequest());
        }

        if (!await IsVisibleAsync(AcquisitionAccessNames.WorkType(kind), cancellationToken))
        {
            return (null, NotFound());
        }

        if (!(await requests.GetCapabilitiesAsync(kind, cancellationToken)).CanRequest)
        {
            return (null, Forbid());
        }

        if (kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
        {
            if (!string.Equals(provider, TmdbDiscoveryProvider.ProviderKey, StringComparison.Ordinal)
                || !TmdbDiscoveryProvider.TryNormalizeExternalId(externalId, out var tmdbId))
            {
                return (null, BadRequest());
            }

            try
            {
                var mediaType = kind == MediaAcquisitionKind.Movie ? TmdbDiscoveryMediaType.Movie : TmdbDiscoveryMediaType.Series;
                var work = await tmdb.EnsureCanonicalWorkAsync(mediaType, tmdbId, cancellationToken);
                return (new RequestTarget(kind, TmdbDiscoveryProvider.ProviderKey, tmdbId, work), null);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or InvalidDataException)
            {
                logger.LogWarning(exception, "TMDB identity {Kind}/{ExternalId} could not be materialized before request.", kind, externalId);
                return (null, StatusCode(StatusCodes.Status503ServiceUnavailable));
            }
        }

        if (kind == MediaAcquisitionKind.Book)
        {
            return (new RequestTarget(kind, Jularr.Web.Features.Books.BookCatalogService.CatalogRequestProvider, externalId.Trim(), null), null);
        }

        if (!string.Equals(provider, AniListMetadataProvider.ProviderKey, StringComparison.Ordinal)
            || !int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId)
            || aniListId <= 0)
        {
            return (null, BadRequest());
        }

        return (new RequestTarget(kind, AniListMetadataProvider.ProviderKey, aniListId.ToString(CultureInfo.InvariantCulture), null), null);
    }

    private sealed record RequestTarget(MediaAcquisitionKind Kind, string Provider, string ExternalId, Work? Work);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? OfferedLanguage(string? tag) =>
        tag is not null && (tag == PlaybackLanguages.SubtitlesOff || RequestLanguages.Choices.Any(choice => choice.Tag == tag)) ? tag : null;

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
        if (request is null)
        {
            return NotFound();
        }

        return new JsonResult(new
        {
            requestId = request.Id,
            status = AcquisitionAccessNames.Status(request.Status),
            progress = await RequestProgressAsync(request, cancellationToken),
            resultUrl = request.ResultUrl,
            done = request.Status is AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Failed
        });
    }

    /// <summary>
    /// The whole percent of a request's transfer, only while the download reports a trustworthy size; null otherwise. A status never stands
    /// for a percentage, and the message of an operation or request is a technical text a consumer is not shown.
    /// </summary>
    private async Task<int?> RequestProgressAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.Status != AcquisitionRequestStatus.Downloading || request.OperationId is not { } operationId)
        {
            return null;
        }

        return ConsumerAcquisitionProjector.ReliableProgress(await new OperationStore(db).GetAsync(operationId, cancellationToken));
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
