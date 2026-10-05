using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

internal sealed class EpisodeFlowFixture : IAsyncDisposable
{
    private readonly string path;
    private readonly List<string> mediaPaths = [];
    private LibraryRoot? root;

    private EpisodeFlowFixture(string path, AppDbContext db)
    {
        this.path = path;
        Db = db;
    }

    public AppDbContext Db { get; private set; }

    public static async Task<EpisodeFlowFixture> CreateAsync(bool migrate = true)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jularr-episode-flow-{Guid.NewGuid():N}.db");
        var fixture = new EpisodeFlowFixture(path, CreateContext(path));

        if (migrate)
        {
            await DatabaseMigrationBridge.UpgradeAsync(fixture.Db);
        }

        return fixture;
    }

    public async Task ReopenAsync()
    {
        await Db.DisposeAsync();
        Db = CreateContext(path);
    }

    public EpisodeProgressService Service(string profileId) =>
        ProgressService(Db, Account(profileId));

    /// <summary>The legacy-identity adapter wired to the canonical video owners, as the application composes it.</summary>
    public static EpisodeProgressService ProgressService(AppDbContext db, CurrentAccountContext account) =>
        new(db, account, new VideoProgressService(db), new CanonicalVideoTargetResolver(db, new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db))));

    /// <summary>The Home page wired like the application: progress through the shared adapter.</summary>
    public static IndexModel Home(AppDbContext db, CurrentAccountContext account) =>
        new(db, account, ProgressService(db, account));

    public async Task<Anime> AddAnimeAsync(string key)
    {
        var anime = new Anime { Key = key, Title = key };
        Db.Add(anime);
        await Db.SaveChangesAsync();
        return anime;
    }

    public async Task<Episode> AddEpisodeAsync(
        Anime anime,
        int seasonNumber,
        int number,
        bool withMedia = true,
        Guid? id = null)
    {
        var episode = new Episode
        {
            Id = id ?? Guid.NewGuid(),
            AnimeId = anime.Id,
            SeasonNumber = seasonNumber,
            Number = number,
            Title = $"Episode {number}"
        };
        Db.Add(episode);

        if (withMedia)
        {
            if (root is null)
            {
                root = new LibraryRoot { Name = "Test", Path = Path.GetTempPath() };
                Db.Add(root);
            }

            var mediaPath = Path.Combine(
                Path.GetTempPath(),
                $"{anime.Key}-s{seasonNumber:00}e{number:00}-{Guid.NewGuid():N}.mkv");
            await File.WriteAllBytesAsync(mediaPath, [0]);
            mediaPaths.Add(mediaPath);
            Db.Add(new MediaFile
            {
                LibraryRootId = root.Id,
                EpisodeId = episode.Id,
                Path = mediaPath,
                SizeBytes = 1,
                LastWriteTimeUtc = DateTime.UtcNow
            });
        }

        await Db.SaveChangesAsync();
        if (withMedia)
        {
            // Canonical Continue Watching only offers episodes that have a canonical video Asset.
            await CanonicalProgressSeed.AttachCanonicalVideoAsync(Db);
        }

        return episode;
    }

    /// <summary>Moves the canonical progress row of one legacy episode to a fixed timestamp.</summary>
    public async Task SetUpdatedAtAsync(
        string profileId,
        Guid episodeId,
        DateTime updatedAt)
    {
        var updated = await Db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "MediaProgress" p
            SET "UpdatedAt" = {0}
            FROM "WorkEpisodes" we, "WorkSourceLinks" l, "Episodes" e
            WHERE p."ProfileId" = {1}
              AND p."WorkEpisodeId" = we."Id"
              AND l."SourceKind" = {2}
              AND l."WorkId" = we."WorkId"
              AND e."Id" = {3}
              AND e."AnimeId" = l."SourceId"
              AND e."SeasonNumber" = we."SeasonNumber"
              AND e."Number" = we."EpisodeNumber"
            """,
            updatedAt,
            profileId,
            (int)WorkSourceKind.Anime,
            episodeId);
        Assert.AreEqual(1, updated, "Expected exactly one canonical progress row.");
    }

    public static CurrentAccountContext Account(string profileId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, profileId)],
                    "test"))
        };

        return new CurrentAccountContext(
            new FixedHttpContextAccessor { HttpContext = httpContext });
    }

    // HttpContextAccessor stores its context in a shared AsyncLocal, which
    // would let the last created profile win for every service in a test.
    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        File.Delete(path);
        foreach (var mediaPath in mediaPaths)
        {
            File.Delete(mediaPath);
        }
    }

    private static AppDbContext CreateContext(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options);
}
