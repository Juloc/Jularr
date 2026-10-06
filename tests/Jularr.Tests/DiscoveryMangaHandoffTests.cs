using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Manga;
using Jularr.Web.Pages.Discover;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class DiscoveryMangaHandoffTests
{
    [TestMethod]
    public async Task CanonicalMangaLibraryResolvesAniListExternalId()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "jularr.db");

        try
        {
            await using var db = await CreateDatabaseAsync(database);
            var repository = new MangaRepository(db);
            var seriesId = Guid.NewGuid();

            await repository.UpsertSeriesAsync(
                seriesId,
                "Local Manga",
                Path.Combine(root, "manga"),
                CancellationToken.None);

            await repository.UpdateMetadataAsync(
                seriesId,
                new MangaAniListCandidate(
                    "12345",
                    "Matched Manga",
                    "マッチした漫画",
                    null,
                    null,
                    null,
                    "RELEASING"),
                CancellationToken.None);

            var matches = await repository.GetAniListMatchesAsync(
                ["12345", "99999"],
                CancellationToken.None);

            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual(seriesId, matches["12345"]);
            Assert.IsFalse(matches.ContainsKey("99999"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void MangaImportUrlCarriesAniListIdentityAndTitle()
    {
        var url = DiscoveryCoordinator.BuildMangaImportUrl(
            "12345",
            "Manga title / 日本語");

        Assert.IsTrue(
            url.StartsWith(
                "/Discover/MangaImport?anilistId=12345",
                StringComparison.Ordinal));
        StringAssert.Contains(url, "title=Manga%20title%20%2F%20");
    }

    [TestMethod]
    public void MangaHandoffValidatesAniListIdentity()
    {
        Assert.IsTrue(
            MangaImportModel.TryNormalizeSelection(
                "12345",
                "  Example Manga  ",
                out var id,
                out var title));
        Assert.AreEqual("12345", id);
        Assert.AreEqual("Example Manga", title);

        Assert.IsFalse(
            MangaImportModel.TryNormalizeSelection(
                "../123",
                "Example",
                out _,
                out _));
        Assert.IsFalse(
            MangaImportModel.TryNormalizeSelection(
                "0",
                "Example",
                out _,
                out _));
    }

    [TestMethod]
    public void DiscoveryUsesCanonicalMangaRouteInsteadOfNovelRoute()
    {
        var root = FindRepositoryRoot();
        var coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Features",
            "Discovery",
            "DiscoveryCoordinator.cs"));

        StringAssert.Contains(
            coordinator,
            "LocalUrl = $\"/Manga/Series/{seriesId}\"");
        StringAssert.Contains(
            coordinator,
            "new MangaRepository(db).GetAniListMatchesAsync");
        StringAssert.Contains(
            coordinator,
            "item.Category == \"light-novel\" &&");
        StringAssert.Contains(
            coordinator,
            "item.Category == \"manga\" &&");
    }

    [TestMethod]
    public void DiscoveryClientOffersMangaImportAction()
    {
        var root = FindRepositoryRoot();
        var card = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Shared",
            "_DiscoverCard.cshtml"));

        // The owner's manga import link is decided on the server and localized; the card only renders it.
        StringAssert.Contains(card, "card.ImportMangaUrl is { } importUrl");
        StringAssert.Contains(card, "discover.preview.addManga");
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Jularr repository root.");
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jularr-discovery-manga-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
