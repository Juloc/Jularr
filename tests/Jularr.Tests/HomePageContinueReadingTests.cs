using Jularr.Web.Features.Discovery;
using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Reading;
using Jularr.Web.Pages;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

/// <summary>
/// #338: Home shows the profile's resumable reading progress — merged into the one Continue row
/// of the approved mockup — only when it exists, without Learning data.
/// </summary>
[TestClass]
public sealed class HomePageContinueReadingTests
{
    private const string Profile = "home-reader";

    [TestMethod]
    public async Task HomeHasNoContinueReadingWithoutProgress()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        var novel = await fixture.SeedNovelAsync("Someone else's novel", chapters: 2);
        await fixture.SetNovelProgressAsync("other-profile", novel, 0, 300, DateTime.UtcNow);

        var home = Home(fixture);
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.AreEqual(0, home.ContinueReading.Count);
    }

    [TestMethod]
    public async Task HomeLoadsTheProfilesContinueReadingNewestFirst()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        var manga = await fixture.SeedMangaAsync("Manga", (1, 10));
        var now = DateTime.UtcNow;
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 300, now.AddMinutes(-5));
        await fixture.SetMangaProgressAsync(Profile, manga, 0, 3, now);

        var home = Home(fixture);
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { ContinueReadingKind.Manga, ContinueReadingKind.Novel },
            home.ContinueReading.Select(x => x.Kind).ToArray());
        Assert.AreEqual($"/Manga/Read/{manga.ChapterIds[0]}?page=3", home.ContinueReading[0].ResumeUrl);
        Assert.AreEqual($"/Novels/Read/{novel.ChapterIds[0]}", home.ContinueReading[1].ResumeUrl);
    }

    [TestMethod]
    public async Task ReadingJoinsTheContinueRowAtItsResumePosition()
    {
        await using var fixture = await ContinueReadingQueryTests.ContinueReadingFixture.CreateAsync();
        var novel = await fixture.SeedNovelAsync("Novel", chapters: 2);
        await fixture.SetNovelProgressAsync(Profile, novel, 0, 300, DateTime.UtcNow);

        var home = Home(fixture);
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        var tile = Assert.ContainsSingle(home.ContinueTiles);
        Assert.AreEqual(home.ContinueReading[0].ResumeUrl, tile.Href);
        Assert.AreEqual(home.Ui["home.continueReading"], home.ContinueHeading, "A reading-only row keeps the Continue Reading heading.");
        Assert.AreEqual(home.ContinueReadingDiscoverUrl, home.ContinueDiscoverUrl);
    }

    [TestMethod]
    public void ContinueRowRendersOnlyWhenItemsExistWithoutLearningData()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Index.cshtml"));

        // Watching and reading share one Continue row (docs/mockups/home), rendered only with items.
        var guard = view.IndexOf("@if (Model.ContinueTiles.Count > 0)", StringComparison.Ordinal);
        var row = view.IndexOf("data-home-continue", StringComparison.Ordinal);
        var next = view.IndexOf("</section>", row, StringComparison.Ordinal);
        Assert.IsTrue(guard > 0 && row > guard, "The row is rendered only when items exist.");
        Assert.IsTrue(next > row);

        var section = view[guard..next];
        StringAssert.Contains(section, "<partial name=\"_HomeContinueTile\"");
        StringAssert.Contains(section, "Model.ContinueHeading");
        Assert.IsFalse(section.Contains("/Learn", StringComparison.Ordinal), "No Learning prompts.");
        Assert.IsFalse(section.Contains("Learning", StringComparison.Ordinal), "No Learning data.");
    }

    [TestMethod]
    public void ContinueReadingKeysAreInTheTranslationCatalog()
    {
        string[] keys =
        [
            "home.continueReading",
            "home.continueReading.eyebrow",
            "home.continueReading.kind.novel",
            "home.continueReading.kind.book",
            "home.continueReading.kind.manga",
            "home.continueReading.chapter",
            "home.continueReading.chapterPage",
            "home.continueReading.progressAria"
        ];

        foreach (var key in keys)
        {
            Assert.IsTrue(UiTranslationResources.TryGet(key, out _), key);
        }
    }

    private static IndexModel Home(ContinueReadingQueryTests.ContinueReadingFixture fixture)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Profile)],
                    "test"))
        };

        return EpisodeFlowFixture.Home(
            fixture.Db,
            new CurrentAccountContext(new HttpContextAccessor { HttpContext = httpContext }));
    }

    private static string RepositoryRoot()
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

        throw new InvalidOperationException("Repository root not found.");
    }
}
