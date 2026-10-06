using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Tracking;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// Loads the titles Discover shows. Provider calls go through <see cref="DiscoverySourceFlights"/> (one call per source, shared and cached
/// there); this class decides which sources a scope needs for a viewer, waits for them within the budget it is given and overlays the
/// library state of the titles that came back. The order of the titles follows the sources, never the order in which they answered.
/// </summary>
public sealed class DiscoveryCoordinator(
    TmdbCredentialStore tmdbCredentials,
    AniListAccountService aniListAccount,
    AppDbContext db,
    DiscoverySourceFlights flights,
    TimeProvider clock,
    ILogger<DiscoveryCoordinator> logger,
    IInstanceModuleService? instanceModules = null) : IDiscoveryFeed
{
    // A shelf is a bounded preview of a view; the view itself loads full provider pages as the viewer scrolls.
    private const int AnimeLimit = 10;
    private const int ReadingLimit = 14;
    private const int BookLimit = 10;
    private const int TmdbLimit = 12;

    private static readonly TimeSpan BrowseFreshness = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SearchFreshness = TimeSpan.FromSeconds(45);

    /// <param name="Blocked">Set when the source cannot be asked at all (TMDB has no usable credential): the call has no fetch and settles at once with this state.</param>
    private sealed record SourceCall(DiscoverySource Source, string Key, TimeSpan Freshness, DiscoverySourceFetch? Fetch, DiscoverySourceState? Blocked = null);

    public async Task<DiscoveryLoad> LoadAsync(IReadOnlyList<DiscoveryRequest> requests, DiscoveryAudience audience, DiscoveryWait wait, CancellationToken cancellationToken)
    {
        var instance = instanceModules is null ? InstanceModuleSettings.Default : await instanceModules.GetAsync(cancellationToken);
        var tmdbAvailability = (await tmdbCredentials.GetAsync(cancellationToken)).Availability;
        var started = new Dictionary<string, DiscoverySourceFlight>(StringComparer.Ordinal);
        var plans = new List<(DiscoveryRequest Request, IReadOnlyList<SourceCall> Calls)>(requests.Count);
        var personal = new Dictionary<int, DiscoveryBatch>();

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            if (request.RequiresPersonalAniListAccount)
            {
                personal[index] = await LoadMyListAsync(request, audience, instance, cancellationToken);
                plans.Add((request, []));
                continue;
            }

            var calls = CallsFor(request, audience, instance, tmdbAvailability);
            foreach (var call in calls)
            {
                if (call.Blocked is null && !started.ContainsKey(call.Key))
                {
                    started[call.Key] = flights.Start(call.Source, call.Key, call.Freshness, wait.Refresh?.Contains(call.Source) == true, call.Fetch!);
                }
            }

            plans.Add((request, calls));
        }

        // A retry waits for the sources the viewer asked about and for nothing else: a slow source elsewhere on the page must not hold its answer back.
        var awaited = wait.Refresh is null
            ? [.. started.Values]
            : plans.SelectMany(plan => plan.Calls).Where(call => call.Blocked is null && wait.Refresh.Contains(call.Source)).Select(call => started[call.Key]).Distinct().ToArray();
        await WaitForSourcesAsync(awaited, wait, cancellationToken);

        var batches = new List<DiscoveryBatch>(requests.Count);
        for (var index = 0; index < plans.Count; index++)
        {
            var (request, calls) = plans[index];
            batches.Add(personal.TryGetValue(index, out var mine) ? mine : ToBatch(request, calls, started, audience, instance));
        }

        var settled = started.Values.Count(flight => flight.IsSettled);
        return new DiscoveryLoad(batches, settled, started.Count - settled);
    }

    private IReadOnlyList<SourceCall> CallsFor(DiscoveryRequest request, DiscoveryAudience audience, InstanceModuleSettings instance, TmdbAvailability tmdbAvailability)
    {
        var calls = new List<SourceCall>();
        foreach (var source in DiscoverySources.For(request.Category))
        {
            // A disabled module or a media type the profile may not browse does not exist for the viewer: nothing is shown for it, not even a notice.
            if (!SourceAvailable(source, audience, instance))
            {
                continue;
            }

            // A source whose provider has no usable credential still shows up, as a settled failure of its own kind, so it can never pass for zero results.
            if (source is DiscoverySource.Movies or DiscoverySource.Series && tmdbAvailability != TmdbAvailability.Ready)
            {
                calls.Add(new SourceCall(source, "", TimeSpan.Zero, null, tmdbAvailability == TmdbAvailability.Disabled ? DiscoverySourceState.Disabled : DiscoverySourceState.NotConfigured));
                continue;
            }

            var key = $"{DiscoverySources.Name(source)}|{request.Category}|{request.Mode}|p{request.Page}{(request.Preview ? "s" : "")}|{request.EffectiveFilter.CacheKey}|{request.Query.ToLowerInvariant()}|{CultureInfo.CurrentUICulture.Name}"
                + (source == DiscoverySource.Reading && audience.IsOwner ? "|owner" : "");
            var freshness = request.Mode == DiscoveryMode.Search ? SearchFreshness : BrowseFreshness;
            calls.Add(new SourceCall(source, key, freshness, FetchFor(source, request, audience.IsOwner)));
        }

        return calls;
    }

    private bool SourceAvailable(DiscoverySource source, DiscoveryAudience audience, InstanceModuleSettings instance) =>
        DiscoverySources.IsVisible(source, audience.VisibleMediaTypes)
        && source switch
        {
            DiscoverySource.Anime => instance.IsEnabled(InstanceModule.Anime),
            DiscoverySource.Movies => instance.IsEnabled(InstanceModule.Movie),
            DiscoverySource.Series => instance.IsEnabled(InstanceModule.Tv),
            DiscoverySource.Reading => instance.IsEnabled(InstanceModule.Manga) || instance.IsEnabled(InstanceModule.Novel),
            _ => instance.IsEnabled(InstanceModule.Book)
        };

    private static DiscoverySourceFetch FetchFor(DiscoverySource source, DiscoveryRequest request, bool isOwner) => source switch
    {
        DiscoverySource.Anime => async (services, cancellationToken) =>
        {
            var provider = services.GetRequiredService<AniListMetadataProvider>();
            var page = await provider.DiscoverPageAsync(AniListOptions(request, AniListDiscoveryKind.Anime, request.Preview ? AnimeLimit : DiscoverySources.PageSize), cancellationToken);
            return page.Items.Select(MapAnime).ToArray();
        },
        DiscoverySource.Reading => async (services, cancellationToken) =>
        {
            var provider = services.GetRequiredService<NovelAniListProvider>();
            var kind = request.Category switch
            {
                DiscoveryCategory.Manga => AniListDiscoveryKind.Manga,
                DiscoveryCategory.LightNovel => AniListDiscoveryKind.LightNovel,
                _ => AniListDiscoveryKind.Reading
            };
            var page = await provider.DiscoverReadingPageAsync(AniListOptions(request, kind, request.Preview ? ReadingLimit : DiscoverySources.PageSize), cancellationToken);
            return page.Items.Select(row => MapReading(row, isOwner)).ToArray();
        },
        DiscoverySource.Movies => (services, cancellationToken) => FetchTmdbAsync(services, TmdbDiscoveryMediaType.Movie, request, cancellationToken),
        DiscoverySource.Series => (services, cancellationToken) => FetchTmdbAsync(services, TmdbDiscoveryMediaType.Series, request, cancellationToken),
        _ => async (services, cancellationToken) =>
        {
            var books = services.GetRequiredService<BookCatalogService>();
            var bookSearch = services.GetService<BookSearchCoordinator>();
            var size = request.Preview ? BookLimit : DiscoverySources.PageSize;
            IReadOnlyList<BookCatalogItem> rows;
            if (request.Mode == DiscoveryMode.Search)
            {
                // The merged book search of several catalogs has no common cursor, so a search is one page.
                rows = request.Page > 1
                    ? []
                    : bookSearch is null
                        ? await books.SearchAsync(request.Query, cancellationToken)
                        : await SearchBooksAsync(bookSearch, request.Query, cancellationToken);
            }
            else
            {
                rows = await books.BrowseAsync(ToBookBrowseMode(request.Mode), cancellationToken, (Math.Max(1, request.Page) - 1) * size, size);
            }

            return rows.Where(row => MatchesGenres(row, request.EffectiveGenres)).Take(size).Select(MapBook).ToArray();
        }
    };

    private static AniListDiscoveryOptions AniListOptions(DiscoveryRequest request, AniListDiscoveryKind kind, int perPage) =>
        new(request.Page, perPage, request.Mode == DiscoveryMode.Search ? request.Query : "", request.Mode, request.EffectiveFilter, kind);

    private static async Task<IReadOnlyList<BookCatalogItem>> SearchBooksAsync(BookSearchCoordinator bookSearch, string query, CancellationToken cancellationToken) =>
        BooksOrFailure(await bookSearch.SearchAsync(query, cancellationToken));

    /// <summary>The books of a search. Catalogs that did not answer and found nothing are a failure, never "no results".</summary>
    public static IReadOnlyList<BookCatalogItem> BooksOrFailure(BookSearchResponse response) =>
        response.Items.Count == 0 && response.Warnings.Any(warning => warning.Source == BookSearchCoordinator.CatalogSource)
            ? throw new HttpRequestException("The book catalogs did not answer.")
            : [.. response.Items.Select(result => result.Book)];

    private static async Task<IReadOnlyList<DiscoveryItem>> FetchTmdbAsync(IServiceProvider services, TmdbDiscoveryMediaType mediaType, DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var provider = services.GetRequiredService<TmdbDiscoveryProvider>();
        var page = await provider.DiscoverPageAsync(mediaType, request, request.Preview ? TmdbLimit : DiscoverySources.TmdbPageSize, cancellationToken);
        return page.Items.Select(MapTmdb).ToArray();
    }

    /// <summary>Waits for the pending sources until the budget is spent, every source settled or, for a follow-up load, one more source settled than the caller already knows.</summary>
    private async Task WaitForSourcesAsync(IReadOnlyList<DiscoverySourceFlight> started, DiscoveryWait wait, CancellationToken cancellationToken)
    {
        if (wait.Budget <= TimeSpan.Zero)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expired = Task.Delay(wait.Budget, clock, budget.Token);
        try
        {
            while (true)
            {
                var open = started.Where(flight => !flight.IsSettled).Select(flight => (Task)flight.Completion).ToList();
                if (open.Count == 0 || (wait.SettledBefore is { } known && started.Count - open.Count > known))
                {
                    return;
                }

                open.Add(expired);
                if (await Task.WhenAny(open) == expired)
                {
                    return;
                }
            }
        }
        finally
        {
            await budget.CancelAsync();
        }
    }

    private static DiscoveryBatch ToBatch(DiscoveryRequest request, IReadOnlyList<SourceCall> calls, IReadOnlyDictionary<string, DiscoverySourceFlight> started, DiscoveryAudience audience, InstanceModuleSettings instance)
    {
        var results = new List<DiscoverySourceResult>(calls.Count);
        foreach (var call in calls)
        {
            if (call.Blocked is { } blocked)
            {
                results.Add(new DiscoverySourceResult(call.Source, blocked, []));
                continue;
            }

            var flight = started[call.Key];
            if (!flight.IsSettled)
            {
                results.Add(new DiscoverySourceResult(call.Source, DiscoverySourceState.Pending, []));
                continue;
            }

            var outcome = flight.Completion.Result;
            IReadOnlyList<DiscoveryItem> items = [.. outcome.Items.Where(item => Includes(request.Category, item.Category) && Allowed(item.Category, audience, instance))];
            results.Add(new DiscoverySourceResult(call.Source, outcome.State, items));
        }

        return new DiscoveryBatch(request, false, results);
    }

    /// <summary>Whether a title of <paramref name="itemCategory"/> belongs to the scope the viewer picked; the reading source answers manga and light novels together.</summary>
    private static bool Includes(DiscoveryCategory scope, string itemCategory) => scope switch
    {
        DiscoveryCategory.All => true,
        DiscoveryCategory.Anime => itemCategory == "anime",
        DiscoveryCategory.Movie => itemCategory == "movie",
        DiscoveryCategory.Series => itemCategory == "tv",
        DiscoveryCategory.Manga => itemCategory == "manga",
        DiscoveryCategory.LightNovel => itemCategory == "light-novel",
        DiscoveryCategory.Book => itemCategory == "book",
        _ => itemCategory is "book" or "light-novel"
    };

    private static bool Allowed(string itemCategory, DiscoveryAudience audience, InstanceModuleSettings instance) => itemCategory switch
    {
        "manga" => instance.IsEnabled(InstanceModule.Manga) && audience.VisibleMediaTypes.Contains(WorkMediaType.Manga),
        "light-novel" => instance.IsEnabled(InstanceModule.Novel) && audience.VisibleMediaTypes.Contains(WorkMediaType.LightNovel),
        _ => true
    };

    /// <summary>My AniList is private to the account: it is read inline for this viewer and never shared through the source flights.</summary>
    public async Task<bool> IsAniListConnectedAsync(CancellationToken cancellationToken) =>
        (await aniListAccount.GetStatusAsync(cancellationToken)).IsConnected;

    private async Task<DiscoveryBatch> LoadMyListAsync(DiscoveryRequest request, DiscoveryAudience audience, InstanceModuleSettings instance, CancellationToken cancellationToken)
    {
        var animeEnabled = instance.IsEnabled(InstanceModule.Anime) && audience.VisibleMediaTypes.Contains(WorkMediaType.Anime);
        var mangaEnabled = instance.IsEnabled(InstanceModule.Manga) && audience.VisibleMediaTypes.Contains(WorkMediaType.Manga);
        var novelEnabled = instance.IsEnabled(InstanceModule.Novel) && audience.VisibleMediaTypes.Contains(WorkMediaType.LightNovel);
        if (!animeEnabled && !mangaEnabled && !novelEnabled)
        {
            return new DiscoveryBatch(request, false, []);
        }

        var status = await aniListAccount.GetStatusAsync(cancellationToken);
        if (!status.IsConnected)
        {
            return new DiscoveryBatch(request, false, []);
        }

        var category = request.Category;
        var results = new List<DiscoverySourceResult>(2);
        if (animeEnabled && category is DiscoveryCategory.All or DiscoveryCategory.Anime)
        {
            results.Add(await CaptureMyListAsync(
                DiscoverySource.Anime,
                async () => (await aniListAccount.GetLibraryAsync(AniListLibraryMediaType.Anime, cancellationToken)).Select(row => MapLibrary(row, audience.IsOwner)).ToArray(),
                cancellationToken));
        }

        if ((mangaEnabled || novelEnabled) && category is DiscoveryCategory.All or DiscoveryCategory.LightNovel or DiscoveryCategory.Manga)
        {
            results.Add(await CaptureMyListAsync(
                DiscoverySource.Reading,
                async () => (await aniListAccount.GetLibraryAsync(AniListLibraryMediaType.Manga, cancellationToken))
                    .Where(row => row.IsNovel ? novelEnabled : mangaEnabled)
                    .Where(row => category switch
                    {
                        DiscoveryCategory.LightNovel => row.IsNovel,
                        DiscoveryCategory.Manga => !row.IsNovel,
                        _ => true
                    })
                    .Select(row => MapLibrary(row, audience.IsOwner))
                    .ToArray(),
                cancellationToken));
        }

        return new DiscoveryBatch(request, true, results);
    }

    private async Task<DiscoverySourceResult> CaptureMyListAsync(DiscoverySource source, Func<Task<IReadOnlyList<DiscoveryItem>>> read, CancellationToken cancellationToken)
    {
        try
        {
            return new DiscoverySourceResult(source, DiscoverySourceState.Ready, await read());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AniListAccountException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The AniList list of {Source} could not be loaded.", source);
            return new DiscoverySourceResult(source, DiscoverySourceState.Unavailable, []);
        }
    }

    /// <summary>
    /// Overlays what the library knows onto the titles (local, matched work, local media id), once for every title of a page and keyed by
    /// <see cref="DiscoveryItem.Id"/>, so a landing of many rows reads the library in one batch instead of once per row. A title that is already a
    /// durable Work shows its persisted title and poster in the profile's metadata language instead of the provider's transient ones.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DiscoveryItem>> OverlayLocalStateAsync(IEnumerable<DiscoveryItem> items, string profileId, CancellationToken cancellationToken)
    {
        var distinct = items.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var overlaid = await ApplyLocalStateAsync(distinct, profileId, cancellationToken);
        return overlaid.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<DiscoveryItem>> ApplyLocalStateAsync(IReadOnlyList<DiscoveryItem> items, string profileId, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var animeIds = items
            .Where(x => x.Category == "anime" && x.Provider == "anilist")
            .Select(x => x.ExternalId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var novelIds = items
            .Where(x =>
                x.Category == "light-novel" &&
                x.Provider == "anilist")
            .Select(x => x.ExternalId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var mangaIds = items
            .Where(x =>
                x.Category == "manga" &&
                x.Provider == "anilist")
            .Select(x => x.ExternalId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var tmdbIds = items
            .Where(x =>
                x.Provider == TmdbDiscoveryProvider.ProviderKey &&
                x.Category is "movie" or "tv")
            .Select(x => x.ExternalId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // A TMDB title is a durable Work as soon as one exists (for example after a Request); it is in the library only once it has a video file.
        var tmdbWorks = tmdbIds.Length == 0
            ? new Dictionary<(WorkMediaType MediaType, string ExternalId), Guid>()
            : (await db.WorkExternalIdentities
                .AsNoTracking()
                .Where(identity => identity.Provider == TmdbDiscoveryProvider.ProviderKey
                    && (identity.MediaType == WorkMediaType.Movie || identity.MediaType == WorkMediaType.Series)
                    && tmdbIds.Contains(identity.ExternalId))
                .Select(identity => new { identity.MediaType, identity.ExternalId, identity.WorkId })
                .ToListAsync(cancellationToken))
                .GroupBy(x => (x.MediaType, x.ExternalId))
                .ToDictionary(x => x.Key, x => x.First().WorkId);

        var tmdbWorkIds = tmdbWorks.Values.Distinct().ToArray();
        var tmdbWorksInLibrary = tmdbWorkIds.Length == 0
            ? new HashSet<Guid>()
            : (await (
                    from asset in db.MediaAssets.AsNoTracking()
                    join file in db.StoredFiles.AsNoTracking()
                        on (Guid?)asset.Id equals file.MediaAssetId
                    where tmdbWorkIds.Contains(asset.WorkId) && asset.Kind == MediaAssetKind.Video
                    select asset.WorkId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

        IReadOnlyDictionary<Guid, WorkCardMetadata> persisted = new Dictionary<Guid, WorkCardMetadata>();
        if (tmdbWorkIds.Length > 0)
        {
            var rows = await new WorkMetadataStore(db).LoadCardMetadataAsync(tmdbWorkIds, cancellationToken);
            persisted = WorkMetadataPresentation.ResolveCards(rows, await WorkMetadataLocales.ForProfileAsync(db, profileId, cancellationToken));
        }

        var animeMatches = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (animeIds.Length > 0)
        {
            var rows = await db.AnimeMetadata
                .AsNoTracking()
                .Where(x =>
                    x.Provider == AniListMetadataProvider.ProviderKey &&
                    animeIds.Contains(x.ExternalId))
                .Select(x => new
                {
                    x.ExternalId,
                    x.AnimeId
                })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                animeMatches.TryAdd(row.ExternalId, row.AnimeId);
            }
        }

        var novelMatches = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (novelIds.Length > 0)
        {
            var rows = await db.NovelWorks
                .AsNoTracking()
                .Where(x =>
                    x.MetadataProvider == NovelAniListProvider.ProviderKey &&
                    x.MetadataExternalId != null &&
                    novelIds.Contains(x.MetadataExternalId))
                .Select(x => new
                {
                    ExternalId = x.MetadataExternalId!,
                    x.Id
                })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                novelMatches.TryAdd(row.ExternalId, row.Id);
            }
        }

        IReadOnlyDictionary<string, Guid> mangaMatches =
            mangaIds.Length == 0
                ? new Dictionary<string, Guid>(StringComparer.Ordinal)
                : await new MangaRepository(db).GetAniListMatchesAsync(
                    mangaIds,
                    cancellationToken);

        return items
            .Select(item =>
            {
                if (item.Category == "anime" &&
                    animeMatches.TryGetValue(item.ExternalId, out var animeId))
                {
                    return item with
                    {
                        IsLocal = true,
                        LocalUrl = $"/Library/Anime/{animeId}",
                        LocalMediaId = animeId
                    };
                }

                if (item.Category == "light-novel" &&
                    novelMatches.TryGetValue(item.ExternalId, out var workId))
                {
                    return item with
                    {
                        IsLocal = true,
                        LocalUrl = $"/Novels/Work/{workId}",
                        LocalMediaId = workId
                    };
                }

                if (item.Category == "manga" &&
                    mangaMatches.TryGetValue(item.ExternalId, out var seriesId))
                {
                    return item with
                    {
                        IsLocal = true,
                        LocalUrl = $"/Manga/Series/{seriesId}",
                        LocalMediaId = seriesId
                    };
                }

                var tmdbType = item.Category switch
                {
                    "movie" => WorkMediaType.Movie,
                    "tv" => WorkMediaType.Series,
                    _ => (WorkMediaType?)null
                };
                if (item.Provider == TmdbDiscoveryProvider.ProviderKey
                    && tmdbType is { } mediaType
                    && tmdbWorks.TryGetValue((mediaType, item.ExternalId), out var canonicalWorkId))
                {
                    persisted.TryGetValue(canonicalWorkId, out var metadata);
                    var detailUrl = LibraryBrowse.DetailHref(mediaType, canonicalWorkId);
                    var inLibrary = tmdbWorksInLibrary.Contains(canonicalWorkId);
                    return item with
                    {
                        IsLocal = inLibrary,
                        LocalUrl = inLibrary ? detailUrl : null,
                        LocalMediaId = inLibrary ? canonicalWorkId : null,
                        WorkUrl = detailUrl,
                        Title = metadata?.Title ?? item.Title,
                        CoverImageUrl = metadata?.PosterUrl ?? item.CoverImageUrl,
                        BackdropUrl = metadata?.BackdropUrl ?? item.BackdropUrl,
                        TrailerKey = metadata?.TrailerKey
                    };
                }

                return item;
            })
            .ToArray();
    }

    private static DiscoveryItem MapTmdb(TmdbDiscoveryCandidate row) =>
        new(
            $"tmdb:{row.Category}:{row.ExternalId}",
            row.Category,
            TmdbDiscoveryProvider.ProviderKey,
            row.ExternalId,
            row.Title,
            row.OriginalTitle,
            row.Description,
            row.CoverImageUrl,
            row.MediaType == TmdbDiscoveryMediaType.Movie ? "MOVIE" : "TV",
            null,
            row.Year,
            null,
            null,
            null,
            null,
            [],
            false,
            null,
            row.DetailsUrl,
            false,
            Rating: row.Rating,
            BackdropUrl: row.BackdropUrl);

    private static DiscoveryItem MapAnime(AnimeMetadataCandidate row) =>
        new(
            $"anilist:anime:{row.ExternalId}",
            "anime",
            row.Provider,
            row.ExternalId,
            row.PreferredTitle,
            row.NativeTitle,
            row.Description,
            row.CoverImageUrl,
            row.Format,
            row.Status,
            row.SeasonYear,
            null,
            row.EpisodeCount,
            null,
            null,
            [],
            false,
            null,
            $"https://anilist.co/anime/{row.ExternalId}",
            false,
            BackdropUrl: row.BannerImageUrl,
            TrailerKey: row.TrailerKey);

    private static DiscoveryItem MapReading(
        AniListReadingMediaCandidate row,
        bool isOwner) =>
        new(
            $"anilist:{(row.IsNovel ? "novel" : "manga")}:{row.ExternalId}",
            row.IsNovel ? "light-novel" : "manga",
            NovelAniListProvider.ProviderKey,
            row.ExternalId,
            row.PreferredTitle,
            row.NativeTitle,
            row.Description,
            row.CoverImageUrl,
            row.Format,
            row.Status,
            row.StartYear,
            null,
            row.ChapterCount,
            row.VolumeCount,
            null,
            row.Genres,
            false,
            null,
            !row.IsNovel && isOwner
                ? BuildMangaImportUrl(row.ExternalId, row.PreferredTitle)
                : $"https://anilist.co/manga/{row.ExternalId}",
            isOwner && row.IsNovel,
            BackdropUrl: row.BannerImageUrl);

    // Only Open Library editions carry subject tags; the other book sources
    // (Google Books, Wikisource, Gutenberg) never populate Subjects here. A
    // genre filter can only be honored where subject data actually exists, so
    // items without subjects are kept rather than dropped or fake-matched.
    /// <summary>A book matches when its subjects cover every selected genre; a book without subjects is not excluded because nothing says it does not match.</summary>
    private static bool MatchesGenres(BookCatalogItem row, IReadOnlyList<string> genres) =>
        genres.Count == 0
        || row.Subjects.Count == 0
        || genres.All(genre => row.Subjects.Any(subject => subject.Contains(genre, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The Books browse row for a shared discovery mode (#371). Other categories keep
    /// their own <see cref="DiscoveryMode.Trending"/>-flag branching; only Books has a source for
    /// a genuine "New" (recently published) signal, so New only exists as a book browse mode.</summary>
    private static BookBrowseMode ToBookBrowseMode(DiscoveryMode mode) =>
        mode switch
        {
            DiscoveryMode.Top or DiscoveryMode.Popular => BookBrowseMode.Popular,
            DiscoveryMode.New => BookBrowseMode.New,
            _ => BookBrowseMode.Trending
        };

    private static DiscoveryItem MapBook(BookCatalogItem row) =>
        new(
            $"book:{row.Id}",
            "book",
            row.SourceName,
            row.Id,
            row.Title,
            null,
            row.Summary,
            row.CoverImageUrl,
            "BOOK",
            null,
            row.FirstPublishYear,
            null,
            null,
            null,
            null,
            row.Subjects.Take(8).ToArray(),
            false,
            null,
            $"/Books/{Uri.EscapeDataString(row.Id)}",
            false,
            Author: row.Author,
            Rating: row.Rating);

    private static DiscoveryItem MapLibrary(
        AniListLibraryMedia row,
        bool isOwner)
    {
        var category = string.Equals(
                row.MediaType,
                "ANIME",
                StringComparison.OrdinalIgnoreCase)
            ? "anime"
            : row.IsNovel
                ? "light-novel"
                : "manga";

        return new DiscoveryItem(
            $"anilist:{category}:{row.MediaId}",
            category,
            "anilist",
            row.MediaId.ToString(),
            row.Title,
            row.NativeTitle,
            null,
            row.CoverImageUrl,
            row.Format,
            row.MediaStatus,
            row.Year,
            row.Progress,
            row.TotalProgress,
            row.VolumeCount,
            row.ListStatus,
            row.Genres,
            false,
            null,
            category == "anime"
                ? $"https://anilist.co/anime/{row.MediaId}"
                : category == "manga" && isOwner
                    ? BuildMangaImportUrl(row.MediaId.ToString(), row.Title)
                    : $"https://anilist.co/manga/{row.MediaId}",
            isOwner && category == "light-novel");
    }

    public static string BuildMangaImportUrl(
        string externalId,
        string title) =>
        $"/Discover/MangaImport?anilistId={Uri.EscapeDataString(externalId)}" +
        $"&title={Uri.EscapeDataString(title)}";
}
