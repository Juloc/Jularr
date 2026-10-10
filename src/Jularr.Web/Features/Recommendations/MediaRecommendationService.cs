using System.Security.Claims;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Recommendations;

/// <summary>
/// Builds the cross-media "For you" board (#428) for one profile. It composes canonical local data only:
/// the profile's reading library (books and light novels, which carry subjects and authors) feeds the
/// content "Because you enjoyed …" shelves, and the followed works' provider relations
/// (<see cref="FranchiseService"/>) feed the cross-media continuation shelves — the source manga/light
/// novel of a followed anime, and so on. Everything is scored by the shared, deterministic
/// <see cref="MediaRecommendationEngine"/> and filtered to the media types the profile may browse
/// (<see cref="IAppShellService"/>). No provider is called on the page load.
/// </summary>
public sealed class MediaRecommendationService(
    AppDbContext db,
    WatchlistStore watchlist,
    WatchlistLibraryResolver libraryResolver,
    FranchiseService franchises,
    IAppShellService shell)
{
    private const int MaxReadingWorks = 500;

    public async Task<MediaRecommendationResult> GetForProfileAsync(
        ClaimsPrincipal? user,
        string profileId,
        CancellationToken cancellationToken,
        MediaRecommendationOptions? options = null)
    {
        var access = await shell.GetMediaAccessAsync(user, cancellationToken);
        var visible = access.VisibleMediaTypes.ToHashSet();
        if (visible.Count == 0)
        {
            return MediaRecommendationResult.Empty;
        }

        var (seeds, candidates, owned) = await LoadReadingLibraryAsync(profileId, cancellationToken);
        var continuations = await LoadContinuationsAsync(profileId, owned, cancellationToken);

        return MediaRecommendationEngine.Build(
            seeds,
            candidates,
            continuations,
            visible,
            owned,
            options ?? MediaRecommendationOptions.Default);
    }

    /// <summary>
    /// The profile's books and light novels split into seeds (works with reading progress) and candidates
    /// (works not yet started). Both share the <c>NovelWorks</c> table; a book is a work whose source is the
    /// Books importer, everything else is a light novel. Subjects and author drive the content scoring.
    /// </summary>
    private async Task<(
        IReadOnlyList<MediaRecommendationSeed> Seeds,
        IReadOnlyList<MediaRecommendationCandidate> Candidates,
        List<RecommendationOwnedEntity> Owned)> LoadReadingLibraryAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var progress = (await db.NovelProgress
                .AsNoTracking()
                .Where(x => x.ProfileId == profileId)
                .Select(x => new { x.WorkId, x.PositionPermille, x.UpdatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.WorkId)
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(row => row.UpdatedAt).First());

        var works = await db.NovelWorks
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAt)
            .Take(MaxReadingWorks)
            .Select(x => new
            {
                x.Id,
                x.SourceProvider,
                Title = x.MetadataTitle ?? x.Title,
                x.Author,
                x.CoverImageUrl,
                x.MetadataGenresJson,
                x.MetadataProvider,
                x.MetadataExternalId,
                x.MetadataStatus
            })
            .ToListAsync(cancellationToken);

        var seeds = new List<MediaRecommendationSeed>();
        var candidates = new List<MediaRecommendationCandidate>();
        var owned = new List<RecommendationOwnedEntity>();

        foreach (var work in works)
        {
            var isBook = work.SourceProvider == BookCatalogService.ImportedBookProvider;
            var mediaType = isBook ? WorkMediaType.Book : WorkMediaType.LightNovel;
            var subjects = ParseGenres(work.MetadataGenresJson);
            if (subjects.Count == 0 && string.IsNullOrWhiteSpace(work.Author))
            {
                continue;
            }

            var identity = BuildIdentity(mediaType, work.MetadataProvider, work.MetadataExternalId);
            var href = isBook
                ? $"/Books/Library/{work.Id}"
                : $"/Novels/Work/{work.Id}";

            if (progress.TryGetValue(work.Id, out var reading))
            {
                owned.Add(new RecommendationOwnedEntity(
                    RecommendationScoring.Identity(work.Title, work.Author),
                    null));

                seeds.Add(new MediaRecommendationSeed(
                    mediaType,
                    identity,
                    work.Title,
                    work.Author,
                    subjects,
                    IsFinished: reading.PositionPermille >= BookRecommendationProgress.FinishedPositionPermille,
                    reading.UpdatedAt));
            }
            else
            {
                candidates.Add(new MediaRecommendationCandidate(
                    work.Id.ToString(),
                    mediaType,
                    identity,
                    work.Title,
                    work.Author,
                    subjects,
                    work.CoverImageUrl,
                    href,
                    Year: null,
                    IsLocal: true));
            }
        }

        return (seeds, candidates, owned);
    }

    /// <summary>
    /// Cross-media continuation links: for every followed work that can seed a franchise (AniList anime,
    /// manga or light novels), the typed relations already read into <see cref="MediaRelationStore"/> —
    /// adaptations, sequels, side stories — resolved to renderable targets. A followed anime yields its
    /// source manga/light novel; a followed manga yields its adaptation, and so on. Works the profile
    /// already follows are treated as owned so they are never recommended back.
    /// </summary>
    private async Task<IReadOnlyList<MediaContinuationLink>> LoadContinuationsAsync(
        string profileId,
        List<RecommendationOwnedEntity> owned,
        CancellationToken cancellationToken)
    {
        var account = CurrentAccountContext.ForProfile(profileId);
        var visibleTypes = Enum.GetValues<WatchlistMediaType>();
        var links = new List<MediaContinuationLink>();

        // Process SQL pages independently: even a very large watchlist never becomes
        // one unbounded database result or one unbounded relation lookup.
        WatchlistItem? after = null;
        while (true)
        {
            var page = await watchlist.GetEffectivePageAsync(
                account,
                new PageRequest(pageSize: PageRequest.MaximumPageSize),
                visibleTypes,
                cancellationToken,
                after: after);

            var pending = new List<(WatchlistItem Seed, WatchlistDraft Target, string GroupKey)>();
            foreach (var item in page.Items)
            {
                // Every followed work is owned for recommendation purposes.
                owned.Add(new RecommendationOwnedEntity(
                    RecommendationScoring.Identity(item.Title, null),
                    item.Identity.Key));

                if (!FranchiseService.CanSeed(item.Identity))
                {
                    continue;
                }

                var groups = await franchises.GetRelationGroupsAsync(item.Identity, cancellationToken);
                foreach (var group in groups)
                {
                    // Only typed relationships form continuation links. The generic
                    // "same franchise" bucket is not a continuation recommendation.
                    if (group.GroupKey == FranchiseLabels.SameFranchiseGroupKey)
                    {
                        continue;
                    }

                    foreach (var target in group.Items)
                    {
                        if (target.Identity.Key != item.Identity.Key)
                        {
                            pending.Add((item, target, group.GroupKey));
                        }
                    }
                }
            }

            if (pending.Count > 0)
            {
                var matches = await libraryResolver.ResolveAsync(
                    pending.Select(entry => entry.Target.Identity),
                    cancellationToken);

                foreach (var (seedItem, target, groupKey) in pending)
                {
                    var mediaType = WorkMediaTypes.FromWatchlist(target.Identity.MediaType);
                    var href = matches.TryGetValue(target.Identity.Key, out var match)
                        ? match.DetailsUrl
                        : target.Identity.ProviderUrl;
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        continue;
                    }

                    var seed = new MediaRecommendationSeed(
                        WorkMediaTypes.FromWatchlist(seedItem.Identity.MediaType),
                        seedItem.Identity,
                        seedItem.Title,
                        null,
                        [],
                        IsFinished: false,
                        seedItem.AddedAtUtc ?? DateTime.UtcNow);

                    var candidate = new MediaRecommendationCandidate(
                        target.Identity.Key,
                        mediaType,
                        target.Identity,
                        target.Title,
                        null,
                        [],
                        target.CoverImageUrl,
                        href,
                        target.Year,
                        IsLocal: match is not null);

                    links.Add(new MediaContinuationLink(seed, candidate, groupKey));
                }
            }

            if (page.HasMore != true)
            {
                break;
            }

            after = page.Items[^1];
        }

        return links;
    }

    private static WatchlistIdentity? BuildIdentity(
        WorkMediaType mediaType,
        string? provider,
        string? externalId)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        return new WatchlistIdentity(WorkMediaTypes.ToWatchlist(mediaType), provider, externalId);
    }

    private static IReadOnlyList<string> ParseGenres(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { Length: > 0 } genres
                ? genres.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
