using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Games;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class GamesFoundationTests
{
    [TestMethod]
    public async Task MigrationPersistsCanonicalGameWithMultiDiscRelease()
    {
        await using var db = await CreateDbAsync();

        var root = new LibraryRoot { Name = "Games", Path = "/media/games" };
        var game = new Game { CanonicalTitle = "Example RPG", ReleaseYear = 1999 };
        var platform = new GamePlatform { Key = "psx", DisplayName = "PlayStation 1" };
        var release = new GameRelease
        {
            GameId = game.Id,
            GamePlatformId = platform.Id,
            Kind = GameReleaseKind.Clean,
            Region = "US",
            Revision = "Rev 0",
            Format = "cue/bin",
            LanguageTags = ["en"]
        };

        db.LibraryRoots.Add(root);
        db.Games.Add(game);
        db.GamePlatforms.Add(platform);
        db.GameTitles.Add(new GameTitle { GameId = game.Id, Value = "Example RPG International", Kind = GameTitleKind.Alternate });
        db.GameExternalIdentities.Add(new GameExternalIdentity { GameId = game.Id, Provider = "example", ExternalId = "game-1" });
        db.GameReleases.Add(release);
        db.GameReleaseHashes.Add(new GameReleaseHash { GameReleaseId = release.Id, Algorithm = GameHashAlgorithm.Sha1, Value = new string('a', 40), IsPrimary = true });
        db.GameReleaseFiles.AddRange(
            new GameReleaseFile
            {
                GameReleaseId = release.Id,
                LibraryRootId = root.Id,
                RelativePath = "PlayStation 1/Example RPG/US/disc-1.cue",
                SizeBytes = 1024,
                DiscNumber = 1,
                Sequence = 0,
                Role = GameReleaseFileRole.Disc
            },
            new GameReleaseFile
            {
                GameReleaseId = release.Id,
                LibraryRootId = root.Id,
                RelativePath = "PlayStation 1/Example RPG/US/disc-2.cue",
                SizeBytes = 2048,
                DiscNumber = 2,
                Sequence = 1,
                Role = GameReleaseFileRole.Disc
            });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.GameReleases.SingleAsync();
        var files = await db.GameReleaseFiles.OrderBy(x => x.Sequence).ToListAsync();

        Assert.AreEqual(game.Id, stored.GameId);
        Assert.AreEqual(platform.Id, stored.GamePlatformId);
        CollectionAssert.AreEqual(new[] { "en" }, stored.LanguageTags);
        Assert.AreEqual(2, files.Count);
        Assert.AreEqual(1, files[0].DiscNumber);
        Assert.AreEqual(2, files[1].DiscNumber);
        Assert.IsTrue(files.All(x => x.LibraryRootId == root.Id));
    }

    [TestMethod]
    public async Task ProviderIdentityAndReleaseHashAreUniqueEvidence()
    {
        await using var db = await CreateDbAsync();

        var first = new Game { CanonicalTitle = "First" };
        var second = new Game { CanonicalTitle = "Second" };
        var platform = new GamePlatform { Key = "gba", DisplayName = "Game Boy Advance" };
        var firstRelease = new GameRelease { GameId = first.Id, GamePlatformId = platform.Id, Kind = GameReleaseKind.Clean };
        var secondRelease = new GameRelease { GameId = second.Id, GamePlatformId = platform.Id, Kind = GameReleaseKind.Clean };

        db.AddRange(first, second, platform, firstRelease, secondRelease);
        db.GameExternalIdentities.Add(new GameExternalIdentity { GameId = first.Id, Provider = "provider", ExternalId = "same-id" });
        db.GameReleaseHashes.Add(new GameReleaseHash { GameReleaseId = firstRelease.Id, Algorithm = GameHashAlgorithm.Sha1, Value = new string('b', 40) });
        await db.SaveChangesAsync();

        db.GameExternalIdentities.Add(new GameExternalIdentity { GameId = second.Id, Provider = "provider", ExternalId = "same-id" });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.GameReleaseHashes.Add(new GameReleaseHash { GameReleaseId = secondRelease.Id, Algorithm = GameHashAlgorithm.Sha1, Value = new string('b', 40) });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task DatabaseDoesNotCascadeCanonicalGameDeletion()
    {
        await using var db = await CreateDbAsync();

        var game = new Game { CanonicalTitle = "Keep lifecycle explicit" };
        var platform = new GamePlatform { Key = "gb", DisplayName = "Game Boy" };
        var release = new GameRelease { GameId = game.Id, GamePlatformId = platform.Id, Kind = GameReleaseKind.Clean };
        db.AddRange(game, platform, release);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Entry(new Game { Id = game.Id, CanonicalTitle = game.CanonicalTitle }).State = EntityState.Deleted;
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public void GamesModelKeepsRuntimeAndProviderImplementationsOutOfCanonicalEntities()
    {
        var canonicalTypes = new[]
        {
            typeof(Game),
            typeof(GamePlatform),
            typeof(GameRelease),
            typeof(GameReleaseFile)
        };

        foreach (var type in canonicalTypes)
        {
            var propertyTypes = type.GetProperties().Select(x => x.PropertyType.FullName ?? "").ToArray();
            Assert.IsFalse(propertyTypes.Any(x => x.Contains("Emulator", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(propertyTypes.Any(x => x.Contains("Runtime", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(propertyTypes.Any(x => x.Contains(".Pages.", StringComparison.Ordinal)));
            Assert.IsFalse(propertyTypes.Any(x => x.Contains("DownloadClient", StringComparison.Ordinal)));
        }

        var platformProperties = typeof(GamePlatform).GetProperties().Select(x => x.Name).ToArray();
        CollectionAssert.DoesNotContain(platformProperties, "CoreId");
        CollectionAssert.DoesNotContain(platformProperties, "RuntimeKey");
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=games-foundation-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
