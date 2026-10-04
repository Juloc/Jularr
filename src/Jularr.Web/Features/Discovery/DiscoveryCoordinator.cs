using System.Collections.Concurrent;
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

public sealed class DiscoveryCoordinator(
    AniListMetadataProvider animeProvider,
    NovelAniListProvider readingProvider,
    BookCatalogService books,
    TmdbDiscoveryProvider tmdb,
    AniListAccountService aniListAccount,
    AppDbContext db,
    ILogger<DiscoveryCoordinator> logger,
    IInstanceModuleService? instanceModules = null,
    BookSearchCoordinator? bookSearch = null) : IDiscoveryFeed
{
    private const int AnimeLimit = 10;
    private const int ReadingLimit = 14;
    private const int BookLimit = 10;
    private const int TmdbLimit = 12;
    private const int MaximumResultCount = 30;

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.Ordinal);

    public async Task<DiscoveryResponse> GetAsync(
        DiscoveryRequest request,
        string profileId,
        bool isOwner,
        bool includeAniList,
        bool includeBooks,
        CancellationToken cancellationToken)
    {
        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
        var animeEnabled = instance.IsEnabled(InstanceModule.Anime);
        var mangaEnabled = instance.IsEnabled(InstanceModule.Manga);
        var novelEnabled = instance.IsEnabled(InstanceModule.Novel);
        var bookEnabled = instance.IsEnabled(InstanceModule.Book);
        var movieEnabled = instance.IsEnabled(InstanceModule.Movie);
        var tvEnabled = instance.IsEnabled(InstanceModule.Tv);

        if (!CategoryAvailable(
                request.Category,
                animeEnabled,
                mangaEnabled,
                novelEnabled,
                bookEnabled,
                movieEnabled,
                tvEnabled))
        {
            return new DiscoveryResponse(
                request.Query,
                CategoryName(request.Category),
                ModeName(request.Mode),
                request.Genre,
                false,
                [],
                []);
        }

        includeAniList &= animeEnabled || mangaEnabled || novelEnabled;
        includeBooks &= bookEnabled;

        var cacheKey = request.CacheKey(profileId)
            + $"|anilist:{includeAniList}|books:{includeBooks}|tmdb:{tmdb.IsConfigured}"
            + $"|modules:a{animeEnabled}:m{mangaEnabled}:n{novelEnabled}:b{bookEnabled}:movie{movieEnabled}:tv{tvEnabled}";
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var status = AniListAccountStatus.Disconnected;
        var warnings = new List<string>();
        IReadOnlyList<DiscoveryItem> items;

        if (request.RequiresPersonalAniListAccount)
        {
            if (!includeAniList)
            {
                return new DiscoveryResponse(
                    request.Query,
                    CategoryName(request.Category),
                    ModeName(request.Mode),
                    request.Genre,
                    false,
                    [],
                    []);
            }

            status = await aniListAccount.GetStatusAsync(cancellationToken);
            if (!status.IsConnected)
            {
                return new DiscoveryResponse(
                    request.Query,
                    CategoryName(request.Category),
                    ModeName(request.Mode),
                    request.Genre,
                    false,
                    [],
                    ["Connect your AniList account in Settings to browse My AniList."]);
            }

            items = await LoadMyListAsync(
                request.Category,
                isOwner,
                warnings,
                animeEnabled,
                mangaEnabled,
                novelEnabled,
                cancellationToken);
        }
        else
        {
            items = await LoadProviderResultsAsync(
                request,
                isOwner,
                warnings,
                includeAniList,
                includeBooks,
                animeEnabled,
                mangaEnabled,
                novelEnabled,
                bookEnabled,
                movieEnabled,
                tvEnabled,
                cancellationToken);
        }

        items = await ApplyLocalStateAsync(items, cancellationToken);

        var response = new DiscoveryResponse(
            request.Query,
            CategoryName(request.Category),
            ModeName(request.Mode),
            request.Genre,
            status.IsConnected,
            items.Take(MaximumResultCount).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray());

        PutCached(
            cacheKey,
            response,
            request.Mode switch
            {
                DiscoveryMode.MyList => TimeSpan.FromSeconds(20),
                DiscoveryMode.Search => TimeSpan.FromSeconds(45),
                _ => TimeSpan.FromMinutes(3)
            });

        return response;
    }

    private async Task<IReadOnlyList<DiscoveryItem>> LoadProviderResultsAsync(
        DiscoveryRequest request,
        bool isOwner,
        ICollection<string> warnings,
        bool includeAniList,
        bool includeBooks,
        bool animeEnabled,
        bool mangaEnabled,
        bool novelEnabled,
        bool bookEnabled,
        bool movieEnabled,
        bool tvEnabled,
        CancellationToken cancellationToken)
    {
        var includeAnime = includeAniList && animeEnabled &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.Anime);
        var includeNovel = includeAniList && novelEnabled &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.LightNovel or DiscoveryCategory.BooksAndLightNovels);
        var includeManga = includeAniList && mangaEnabled &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.Manga);
        var includeBook = includeBooks && bookEnabled &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.Book or DiscoveryCategory.BooksAndLightNovels);
        var includeMovie = movieEnabled && tmdb.IsConfigured &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.Movie);
        var includeSeries = tvEnabled && tmdb.IsConfigured &&
            (request.Category is DiscoveryCategory.All or DiscoveryCategory.Series);

        var animeTask = includeAnime
            ? CaptureAsync(
                async () =>
                {
                    var rows = request.Mode == DiscoveryMode.Search
                        ? await animeProvider.SearchAsync(
                            request.Query,
                            AnimeLimit,
                            request.Genre,
                            cancellationToken)
                        : await animeProvider.BrowseAsync(
                            request.Mode == DiscoveryMode.Trending,
                            AnimeLimit,
                            request.Genre,
                            cancellationToken);

                    return rows
                        .Select(MapAnime)
                        .ToArray();
                },
                "AniList anime search is temporarily unavailable.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        var readingTask = includeNovel || includeManga
            ? CaptureAsync(
                async () =>
                {
                    var rows = request.Mode == DiscoveryMode.Search
                        ? await readingProvider.SearchReadingMediaAsync(
                            request.Query,
                            ReadingLimit,
                            includeNovel,
                            includeManga,
                            request.Genre,
                            cancellationToken)
                        : await readingProvider.BrowseReadingMediaAsync(
                            request.Mode == DiscoveryMode.Trending,
                            ReadingLimit,
                            includeNovel,
                            includeManga,
                            request.Genre,
                            cancellationToken);

                    return rows
                        .Select(x => MapReading(x, isOwner))
                        .ToArray();
                },
                "AniList novel/manga search is temporarily unavailable.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        var movieTask = includeMovie
            ? CaptureAsync(
                async () =>
                {
                    var rows = request.Mode == DiscoveryMode.Search
                        ? await tmdb.SearchAsync(
                            TmdbDiscoveryMediaType.Movie,
                            request.Query,
                            TmdbLimit,
                            request.Genre,
                            cancellationToken)
                        : await tmdb.BrowseAsync(
                            TmdbDiscoveryMediaType.Movie,
                            request.Mode,
                            TmdbLimit,
                            request.Genre,
                            cancellationToken);

                    return rows.Select(MapTmdb).ToArray();
                },
                "TMDB movie discovery is temporarily unavailable.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        var seriesTask = includeSeries
            ? CaptureAsync(
                async () =>
                {
                    var rows = request.Mode == DiscoveryMode.Search
                        ? await tmdb.SearchAsync(
                            TmdbDiscoveryMediaType.Series,
                            request.Query,
                            TmdbLimit,
                            request.Genre,
                            cancellationToken)
                        : await tmdb.BrowseAsync(
                            TmdbDiscoveryMediaType.Series,
                            request.Mode,
                            TmdbLimit,
                            request.Genre,
                            cancellationToken);

                    return rows.Select(MapTmdb).ToArray();
                },
                "TMDB TV discovery is temporarily unavailable.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        var bookTask = includeBook
            ? CaptureAsync(
                async () =>
                {
                    var rows = request.Mode == DiscoveryMode.Search
                        ? bookSearch is null
                            ? await books.SearchAsync(request.Query, cancellationToken)
                            : (await bookSearch.SearchAsync(request.Query, cancellationToken))
                                .Items
                                .Select(result => result.Book)
                                .ToArray()
                        : await books.BrowseAsync(ToBookBrowseMode(request.Mode), cancellationToken);

                    return rows
                        .Where(row => MatchesGenre(row, request.Genre))
                        .Take(BookLimit)
                        .Select(MapBook)
                        .ToArray();
                },
                "Book search is temporarily unavailable.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        await Task.WhenAll(animeTask, movieTask, seriesTask, readingTask, bookTask);

        return Interleave(
            animeTask.Result,
            movieTask.Result,
            seriesTask.Result,
            readingTask.Result,
            bookTask.Result);
    }

    private async Task<IReadOnlyList<DiscoveryItem>> LoadMyListAsync(
        DiscoveryCategory category,
        bool isOwner,
        ICollection<string> warnings,
        bool animeEnabled,
        bool mangaEnabled,
        bool novelEnabled,
        CancellationToken cancellationToken)
    {
        var includeAnime = animeEnabled
            && (category is DiscoveryCategory.All or DiscoveryCategory.Anime);
        var includeReading = (mangaEnabled || novelEnabled)
            && (category is DiscoveryCategory.All or
                DiscoveryCategory.LightNovel or DiscoveryCategory.Manga or DiscoveryCategory.BooksAndLightNovels);

        var animeTask = includeAnime
            ? CaptureAsync(
                async () =>
                {
                    var rows = await aniListAccount.GetLibraryAsync(
                        AniListLibraryMediaType.Anime,
                        cancellationToken);
                    return rows.Select(x => MapLibrary(x, isOwner)).ToArray();
                },
                "Your AniList anime list could not be loaded.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        var readingTask = includeReading
            ? CaptureAsync(
                async () =>
                {
                    var rows = await aniListAccount.GetLibraryAsync(
                        AniListLibraryMediaType.Manga,
                        cancellationToken);

                    return rows
                        .Where(x => x.IsNovel ? novelEnabled : mangaEnabled)
                        .Where(x => category switch
                        {
                            DiscoveryCategory.LightNovel or DiscoveryCategory.BooksAndLightNovels => x.IsNovel,
                            DiscoveryCategory.Manga => !x.IsNovel,
                            _ => true
                        })
                        .Select(x => MapLibrary(x, isOwner))
                        .ToArray();
                },
                "Your AniList novel/manga list could not be loaded.",
                warnings,
                cancellationToken)
            : Task.FromResult<IReadOnlyList<DiscoveryItem>>([]);

        await Task.WhenAll(animeTask, readingTask);
        return Interleave(animeTask.Result, readingTask.Result);
    }

    private async Task<IReadOnlyList<DiscoveryItem>> ApplyLocalStateAsync(
        IReadOnlyList<DiscoveryItem> items,
        CancellationToken cancellationToken)
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

        var tmdbMatches = tmdbIds.Length == 0
            ? new Dictionary<(WorkMediaType MediaType, string ExternalId), Guid>()
            : (await (
                    from identity in db.WorkExternalIdentities.AsNoTracking()
                    join asset in db.MediaAssets.AsNoTracking()
                        on identity.WorkId equals asset.WorkId
                    join file in db.StoredFiles.AsNoTracking()
                        on (Guid?)asset.Id equals file.MediaAssetId
                    where identity.Provider == TmdbDiscoveryProvider.ProviderKey
                          && (identity.MediaType == WorkMediaType.Movie || identity.MediaType == WorkMediaType.Series)
                          && tmdbIds.Contains(identity.ExternalId)
                          && asset.Kind == MediaAssetKind.Video
                    select new { identity.MediaType, identity.ExternalId, identity.WorkId })
                .Distinct()
                .ToListAsync(cancellationToken))
                .GroupBy(x => (x.MediaType, x.ExternalId))
                .ToDictionary(x => x.Key, x => x.First().WorkId);

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
                    && tmdbMatches.TryGetValue((mediaType, item.ExternalId), out var canonicalWorkId))
                {
                    return item with
                    {
                        IsLocal = true,
                        LocalMediaId = canonicalWorkId
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
            Rating: row.Rating);

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
            false);

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
            isOwner && row.IsNovel);

    // Only Open Library editions carry subject tags; the other book sources
    // (Google Books, Wikisource, Gutenberg) never populate Subjects here. A
    // genre filter can only be honored where subject data actually exists, so
    // items without subjects are kept rather than dropped or fake-matched.
    private static bool MatchesGenre(BookCatalogItem row, string genre) =>
        genre.Length == 0
        || row.Subjects.Count == 0
        || row.Subjects.Any(subject => subject.Contains(genre, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Books browse row for a shared discovery mode (#371). Other categories keep
    /// their own <see cref="DiscoveryMode.Trending"/>-flag branching; only Books has a source for
    /// a genuine "New" (recently published) signal, so New only exists as a book browse mode.</summary>
    private static BookBrowseMode ToBookBrowseMode(DiscoveryMode mode) =>
        mode switch
        {
            DiscoveryMode.Top => BookBrowseMode.Popular,
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

    public static void InvalidateCache() =>
        Cache.Clear();

    public static string BuildMangaImportUrl(
        string externalId,
        string title) =>
        $"/Discover/MangaImport?anilistId={Uri.EscapeDataString(externalId)}" +
        $"&title={Uri.EscapeDataString(title)}";

    private async Task<IReadOnlyList<DiscoveryItem>> CaptureAsync(
        Func<Task<IReadOnlyList<DiscoveryItem>>> action,
        string warning,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or
            NovelMetadataProviderException or
            AniListAccountException or
            HttpRequestException or
            TaskCanceledException or
            InvalidOperationException)
        {
            // The page only shows a generic warning; the cause belongs in the log.
            logger.LogWarning(exception, "Discovery provider failed: {Warning}", warning);

            lock (warnings)
            {
                warnings.Add(warning);
            }

            return [];
        }
    }

    private static IReadOnlyList<DiscoveryItem> Interleave(
        params IReadOnlyList<DiscoveryItem>[] groups)
    {
        var result = new List<DiscoveryItem>();
        var index = 0;

        while (result.Count < MaximumResultCount)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (index >= group.Count)
                {
                    continue;
                }

                result.Add(group[index]);
                added = true;

                if (result.Count >= MaximumResultCount)
                {
                    break;
                }
            }

            if (!added)
            {
                break;
            }

            index++;
        }

        return result;
    }

    private static bool TryGetCached(
        string key,
        out DiscoveryResponse response)
    {
        if (Cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                response = entry.Response;
                return true;
            }

            Cache.TryRemove(key, out _);
        }

        response = null!;
        return false;
    }

    private static void PutCached(
        string key,
        DiscoveryResponse response,
        TimeSpan lifetime)
    {
        if (Cache.Count > 256)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in Cache)
            {
                if (item.Value.ExpiresAt <= now)
                {
                    Cache.TryRemove(item.Key, out _);
                }
            }
        }

        Cache[key] = new CacheEntry(
            response,
            DateTimeOffset.UtcNow.Add(lifetime));
    }

    private bool CategoryAvailable(
        DiscoveryCategory category,
        bool animeEnabled,
        bool mangaEnabled,
        bool novelEnabled,
        bool bookEnabled,
        bool movieEnabled,
        bool tvEnabled) =>
        category switch
        {
            DiscoveryCategory.Anime => animeEnabled,
            DiscoveryCategory.Movie => movieEnabled && tmdb.IsConfigured,
            DiscoveryCategory.Series => tvEnabled && tmdb.IsConfigured,
            DiscoveryCategory.Manga => mangaEnabled,
            DiscoveryCategory.LightNovel => novelEnabled,
            DiscoveryCategory.Book => bookEnabled,
            DiscoveryCategory.BooksAndLightNovels => bookEnabled || novelEnabled,
            _ => animeEnabled || mangaEnabled || novelEnabled || bookEnabled
                || (movieEnabled && tmdb.IsConfigured)
                || (tvEnabled && tmdb.IsConfigured)
        };

    private static string CategoryName(DiscoveryCategory category) =>
        category switch
        {
            DiscoveryCategory.Anime => "anime",
            DiscoveryCategory.Movie => "movie",
            DiscoveryCategory.Series => "tv",
            DiscoveryCategory.LightNovel => "light-novel",
            DiscoveryCategory.Manga => "manga",
            DiscoveryCategory.Book => "book",
            DiscoveryCategory.BooksAndLightNovels => "books-light-novels",
            _ => "all"
        };

    private static string ModeName(DiscoveryMode mode) =>
        mode switch
        {
            DiscoveryMode.Top => "top",
            DiscoveryMode.MyList => "my-list",
            DiscoveryMode.Search => "search",
            DiscoveryMode.New => "new",
            DiscoveryMode.Upcoming => "upcoming",
            _ => "trending"
        };

    private sealed record CacheEntry(
        DiscoveryResponse Response,
        DateTimeOffset ExpiresAt);
}
