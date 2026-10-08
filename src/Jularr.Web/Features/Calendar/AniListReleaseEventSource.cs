using System.Data;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Tracking;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Calendar;

/// <summary>A manga series or novel work linked to an AniList entry.</summary>
public sealed record ReadingReleaseLink(
    ReleaseMediaType MediaType,
    Guid MediaId,
    string Title,
    string? CoverImageUrl,
    string ExternalId,
    string? Status);

public static class ReleaseLibraryLinks
{
    /// <summary>Manga series and light novels of the library that are matched to AniList.</summary>
    public static async Task<IReadOnlyList<ReadingReleaseLink>> LoadReadingLinksAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var links = new List<ReadingReleaseLink>();
        var novels = await db.NovelWorks
            .AsNoTracking()
            .Where(work =>
                work.MetadataProvider == AniListReleaseNormalizer.Provider &&
                work.MetadataExternalId != null &&
                work.SourceProvider != BookCatalogService.ImportedBookProvider)
            .Select(work => new { work.Id, Title = work.MetadataTitle ?? work.Title, work.CoverImageUrl, work.MetadataExternalId, work.MetadataStatus })
            .ToListAsync(cancellationToken);
        links.AddRange(novels.Select(work => new ReadingReleaseLink(
            ReleaseMediaType.LightNovel, work.Id, work.Title, work.CoverImageUrl, work.MetadataExternalId!, work.MetadataStatus)));

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "Id", COALESCE("MetadataTitle", "Title"), "CoverImageUrl", "MetadataExternalId", "MetadataStatus"
                FROM "MangaSeries"
                WHERE "MetadataProvider" = @provider AND "MetadataExternalId" IS NOT NULL;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@provider";
            parameter.Value = AniListReleaseNormalizer.Provider;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                links.Add(new ReadingReleaseLink(
                    ReleaseMediaType.Manga,
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }

        return links;
    }
}

/// <summary>
/// Anime episodes and premieres, and manga/light-novel series starts, from the cached AniList
/// release data, linked to the library entries that are matched to those AniList ids.
/// </summary>
public sealed class AniListReleaseEventSource(
    AppDbContext db,
    ReleaseCalendarCacheStore cache,
    AniListAccountStore mappingStore,
    AnimeEpisodeStates episodeStates,
    AnimeMonitoring animeMonitoring) : IReleaseEventSource
{
    public string Name => "anilist";

    public IReadOnlyCollection<ReleaseMediaType> MediaTypes { get; } =
        [ReleaseMediaType.Anime, ReleaseMediaType.Manga, ReleaseMediaType.LightNovel];

    public async Task<IReadOnlyList<ReleaseEvent>> GetEventsAsync(ReleaseEventQuery query, CancellationToken cancellationToken)
    {
        // Stored ranges are UTC days; one extra day on each side covers every viewer time zone.
        var releases = await cache.GetReleasesAsync(
            AniListReleaseNormalizer.Provider,
            query.Start.AddDays(-1),
            query.End.AddDays(1),
            query.IncludeUndated,
            null,
            cancellationToken);
        releases = releases.Where(release => query.Includes(release.Date)).ToArray();
        if (releases.Count == 0)
        {
            return [];
        }

        var events = new List<ReleaseEvent>();
        if (query.Wants(ReleaseMediaType.Anime))
        {
            events.AddRange(await AnimeEventsAsync(releases, query, cancellationToken));
        }

        if (query.Wants(ReleaseMediaType.Manga) || query.Wants(ReleaseMediaType.LightNovel))
        {
            var starts = releases.Where(release => release.Kind == ReleaseKind.SeriesStart).ToLookup(release => release.ExternalId);
            if (starts.Count > 0)
            {
                foreach (var link in await ReleaseLibraryLinks.LoadReadingLinksAsync(db, cancellationToken))
                {
                    if (!query.Wants(link.MediaType, link.MediaId))
                    {
                        continue;
                    }

                    foreach (var release in starts[link.ExternalId])
                    {
                        events.Add(new ReleaseEvent(
                            ReleaseEvent.BuildId(link.MediaType, link.MediaId, release.Kind, null, release.ExternalId),
                            link.MediaType,
                            link.MediaId,
                            null,
                            release.Kind,
                            link.Title,
                            null,
                            release.Date,
                            release.Provider,
                            release.ExternalId,
                            ReleaseLocalStatus.InLibraryOnly,
                            link.CoverImageUrl));
                    }
                }
            }
        }

        return events;
    }

    private async Task<IReadOnlyList<ReleaseEvent>> AnimeEventsAsync(
        IReadOnlyList<CachedRelease> releases,
        ReleaseEventQuery query,
        CancellationToken cancellationToken)
    {
        var ids = releases
            .Where(release => release.Kind is ReleaseKind.Episode or ReleaseKind.SeasonPremiere)
            .Select(release => release.ExternalId)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return [];
        }

        var mappings = (await mappingStore.LoadAllEpisodeMappingsAsync(cancellationToken))
            .Where(mapping => mapping.Provider.Equals(AniListReleaseNormalizer.Provider, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matched = await db.AnimeMetadata
            .AsNoTracking()
            .Where(item => item.Provider == AniListReleaseNormalizer.Provider && ids.Contains(item.ExternalId))
            .Select(item => item.AnimeId)
            .ToListAsync(cancellationToken);
        var animeIds = matched
            .Concat(mappings.Where(mapping => ids.Contains(mapping.ExternalId)).Select(mapping => mapping.AnimeId))
            .Where(id => query.MediaId is null || id == query.MediaId)
            .ToHashSet();
        if (animeIds.Count == 0)
        {
            return [];
        }

        var anime = await db.Anime
            .AsNoTracking()
            .Where(item => animeIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Key, item.Title })
            .ToListAsync(cancellationToken);
        var metadata = await db.AnimeMetadata
            .AsNoTracking()
            .Where(item => animeIds.Contains(item.AnimeId))
            .Select(item => new { item.AnimeId, item.Provider, item.ExternalId, item.PreferredTitle, item.CoverImageUrl })
            .ToListAsync(cancellationToken);
        var episodes = await db.Episodes
            .AsNoTracking()
            .Where(episode => animeIds.Contains(episode.AnimeId))
            .Select(episode => new
            {
                episode.Id,
                episode.AnimeId,
                episode.SeasonNumber,
                episode.Number,
                HasFile = db.MediaFiles.Any(file => file.EpisodeId == episode.Id)
            })
            .ToListAsync(cancellationToken);

        var library = anime.Select(item =>
        {
            var match = metadata.FirstOrDefault(entry => entry.AnimeId == item.Id);
            return new AnimeReleaseLibraryEntry(
                item.Id,
                item.Key,
                string.IsNullOrWhiteSpace(match?.PreferredTitle) ? item.Title : match.PreferredTitle,
                match?.CoverImageUrl,
                match?.Provider == AniListReleaseNormalizer.Provider ? match.ExternalId : null,
                mappings.Where(mapping => mapping.AnimeId == item.Id).ToArray(),
                episodes
                    .Where(episode => episode.AnimeId == item.Id)
                    .GroupBy(episode => (Season: episode.SeasonNumber, Episode: episode.Number))
                    .ToDictionary(
                        group => group.Key,
                        group => new AnimeReleaseLocalEpisode(group.First().Id, group.Any(episode => episode.HasFile))));
        }).ToArray();

        var states = await episodeStates.LoadAsync(null, cancellationToken);
        var views = await animeMonitoring.LoadAsync([.. library.Select(entry => entry.AnimeKey)], cancellationToken);
        return AnimeReleaseStateResolver.ToEvents(releases, library, states, views, query.Now, query.Zone);
    }
}
