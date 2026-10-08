using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Search;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tv;
using Jularr.Web.Features.Watchlist;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// A migrated PostgreSQL-backed database plus the stores the global search reads (#434): seed helpers
/// for every searchable media type, the media-core bridge, franchises, the anime monitoring store and
/// open acquisition requests.
/// </summary>
internal sealed class GlobalSearchFixture : IAsyncDisposable
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string dataRoot;
    private LibraryRoot? root;

    private GlobalSearchFixture(AppDbContext db, string dataRoot)
    {
        Db = db;
        this.dataRoot = dataRoot;
        Monitoring = new MonitoringStore(dataRoot);
        Requests = new AcquisitionAccessStore(db);
        Works = new WorkService(db);
        Bridge = new LegacyWorkBridge(db, Works, new WorkStructureService(db));
        Franchises = new FranchiseStore(db);
    }

    public AppDbContext Db { get; }
    public MonitoringStore Monitoring { get; }
    public AcquisitionAccessStore Requests { get; }
    public WorkService Works { get; }
    public LegacyWorkBridge Bridge { get; }
    public FranchiseStore Franchises { get; }

    public MediaSearchService Service => new(Db, Monitoring, MonitoringTestSupport.Anime(Db), Requests);

    public static async Task<GlobalSearchFixture> CreateAsync()
    {
        var db = await MediaCoreTestSupport.CreateDbAsync();
        var dataRoot = Path.Combine(Path.GetTempPath(), $"jularr-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        return new GlobalSearchFixture(db, dataRoot);
    }

    public Task<MediaSearchPage> SearchAsync(
        string query,
        MediaSearchFilters? filters = null,
        IReadOnlyCollection<WorkMediaType>? visible = null,
        int limit = 40,
        int offset = 0) =>
        Service.SearchAsync(new MediaSearchRequest(query, filters, visible, limit, offset));

    /// <summary>The titles of a page in order, each with the type of its best variant or "Franchise".</summary>
    public static string[] Rows(MediaSearchPage page) =>
        [.. page.Items.Select(item => item.Kind == MediaSearchResultKind.Franchise
            ? $"Franchise:{item.Title}"
            : $"{item.Type}:{item.Title}")];

    public async Task<Anime> AddAnimeAsync(
        string title,
        int? year = null,
        string? externalId = null,
        bool withFile = false,
        params (string Language, MediaStreamKind Kind)[] streams)
    {
        var anime = new Anime { Key = $"key-{Guid.NewGuid():N}", Title = title };
        Db.Anime.Add(anime);
        Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = "anilist",
            ExternalId = externalId ?? Guid.NewGuid().ToString("N"),
            PreferredTitle = title,
            SeasonYear = year
        });

        if (withFile || streams.Length > 0)
        {
            root ??= new LibraryRoot { Name = "Test", Path = Path.GetTempPath() };
            if (Db.Entry(root).State == EntityState.Detached)
            {
                Db.Add(root);
            }

            var episode = new Episode { AnimeId = anime.Id, SeasonNumber = 1, Number = 1, Title = "Episode 1" };
            var file = new MediaFile
            {
                LibraryRootId = root.Id,
                EpisodeId = episode.Id,
                Path = Path.Combine(Path.GetTempPath(), $"{anime.Key}.mkv"),
                SizeBytes = 1,
                LastWriteTimeUtc = Stamp
            };
            Db.Add(episode);
            Db.Add(file);
            Db.MediaAnalyses.Add(new MediaAnalysis
            {
                MediaFileId = file.Id,
                Status = MediaAnalysisStatus.Succeeded,
                ProbeVersion = MediaInventoryService.CurrentProbeVersion,
                SourceSizeBytes = 1,
                SourceLastWriteTimeUtc = Stamp
            });
            for (var index = 0; index < streams.Length; index++)
            {
                Db.MediaAnalysisStreams.Add(new MediaAnalysisStream
                {
                    MediaFileId = file.Id,
                    StreamIndex = index + 1,
                    Kind = streams[index].Kind,
                    Codec = streams[index].Kind == MediaStreamKind.Audio ? "aac" : "ass",
                    Language = streams[index].Language
                });
            }
        }

        await Db.SaveChangesAsync();
        return anime;
    }

    /// <summary>A catalog-less anime row (no provider metadata match yet).</summary>
    public async Task<Anime> AddUnmatchedAnimeAsync(string title)
    {
        var anime = new Anime { Key = $"key-{Guid.NewGuid():N}", Title = title };
        Db.Anime.Add(anime);
        await Db.SaveChangesAsync();
        return anime;
    }

    public async Task<NovelWork> AddNovelAsync(
        string title,
        bool book = false,
        string? format = null,
        string[]? genres = null,
        bool withContent = false,
        string? translatedTo = null,
        string? nativeTitle = null)
    {
        var work = new NovelWork
        {
            SourceProvider = book ? BookCatalogService.ImportedBookProvider : "narou",
            SourceKey = Guid.NewGuid().ToString("N"),
            SourceUrl = $"https://example.test/{Guid.NewGuid():N}",
            Title = title,
            Format = format,
            MetadataNativeTitle = nativeTitle,
            MetadataGenresJson = genres is { Length: > 0 } ? JsonSerializer.Serialize(genres) : null
        };
        Db.NovelWorks.Add(work);

        if (withContent || translatedTo is not null)
        {
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, SourceKey = "v1" };
            var chapter = new NovelChapter
            {
                WorkId = work.Id,
                VolumeId = volume.Id,
                Number = 1,
                SourceUrl = "chapter-1",
                Title = "Chapter 1",
                OriginalText = withContent ? "text" : ""
            };
            Db.NovelVolumes.Add(volume);
            Db.NovelChapters.Add(chapter);
            if (translatedTo is not null)
            {
                Db.NovelTranslations.Add(new NovelTranslation
                {
                    ChapterId = chapter.Id,
                    TargetLanguage = translatedTo,
                    ProviderId = "test",
                    Text = "translated"
                });
            }
        }

        await Db.SaveChangesAsync();
        return work;
    }

    public async Task<Guid> AddMangaAsync(string title, int pages = 0)
    {
        var id = Guid.NewGuid();
        await Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "MangaSeries" ("Id", "Title", "SourcePath", "Direction", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 'rtl', {3}, {3});
            """,
            id.ToString(),
            title,
            $"/data/manga/{id}",
            Stamp.ToString("O"));
        if (pages > 0)
        {
            await Db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "MangaChapters"
                    ("Id", "SeriesId", "Number", "VolumeNumber", "Title", "SourcePath", "SourceKind", "PageCount", "SourceUpdatedAt", "CreatedAt", "UpdatedAt")
                VALUES ({0}, {1}, 1, NULL, 'Chapter 1', {2}, 'cbz', {3}, {4}, {4}, {4});
                """,
                Guid.NewGuid().ToString(),
                id.ToString(),
                $"/data/manga/{id}/1.cbz",
                pages,
                Stamp.ToString("O"));
        }

        return id;
    }

    public async Task<Movie> AddMovieAsync(string title, int? year = null, string? tmdbId = null)
    {
        var movie = new Movie { Key = $"{title}-{year}-{Guid.NewGuid():N}", Title = title, Year = year, TmdbId = tmdbId };
        Db.Movies.Add(movie);
        await Db.SaveChangesAsync();
        return movie;
    }

    public async Task<TvSeries> AddSeriesAsync(string title, int? year = null)
    {
        var series = new TvSeries { Key = $"{title}-{year}-{Guid.NewGuid():N}", Title = title, Year = year };
        Db.TvSeries.Add(series);
        await Db.SaveChangesAsync();
        return series;
    }

    public async Task<Audiobook> AddAudiobookAsync(string title, int? year = null, string? author = null, bool withFile = false)
    {
        var audiobook = new Audiobook { Key = $"{title}-{year}-{Guid.NewGuid():N}", Title = title, Year = year, Author = author };
        Db.Audiobooks.Add(audiobook);
        if (withFile)
        {
            Db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = audiobook.Id,
                FileKey = "part1",
                FileName = "part1.m4b",
                Format = "M4B",
                StoragePath = "/audiobooks/part1.m4b",
                SizeBytes = 1
            });
        }

        await Db.SaveChangesAsync();
        return audiobook;
    }

    /// <summary>A franchise whose seed is the first member; the title is what the provider refresh would set.</summary>
    public async Task<Guid> AddFranchiseAsync(
        string title,
        params (WatchlistMediaType Type, string ExternalId, string MemberTitle)[] members)
    {
        var seed = members[0];
        var franchiseId = await Franchises.GetOrCreateBySeedAsync(
            new WatchlistIdentity(seed.Type, "anilist", seed.ExternalId),
            CancellationToken.None);
        await Franchises.SetTitleAsync(franchiseId, title, CancellationToken.None);
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            await Franchises.UpsertMemberAsync(
                franchiseId,
                new WatchlistDraft(
                    new WatchlistIdentity(member.Type, "anilist", member.ExternalId),
                    member.MemberTitle),
                relationType: index == 0 ? null : "SEQUEL",
                isSeed: index == 0,
                CancellationToken.None);
        }

        return franchiseId;
    }

    public async Task MonitorAnimeAsync(Anime anime, bool wanted = false)
    {
        await MonitoringTestSupport.Anime(Db).SetMonitoredAsync(anime.Id, true, CancellationToken.None);
        await Monitoring.UpdateAsync(state =>
        {
            state.Anime[anime.Key] = new MonitorSettings(anime.Key, false);
            if (wanted)
            {
                var key = MonitoredUnitKey.ForEpisode(anime.Key, 1, 1);
                state.Wanted[key.ToString()] = new WantedUnit(key, WantedReason.Missing, Stamp);
            }

            return state;
        });
    }

    public Task<AcquisitionRequest> OpenRequestAsync(MediaAcquisitionKind kind, string provider, string externalId, string title) =>
        Requests.CreateAsync(
            new AcquisitionRequestDraft(kind, provider, externalId, title, null, null),
            "profile-a",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try
        {
            Directory.Delete(dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
