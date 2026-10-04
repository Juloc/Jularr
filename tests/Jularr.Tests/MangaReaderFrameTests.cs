using System.Text.RegularExpressions;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Manga;
using Jularr.Web.Pages.Manga;

namespace Jularr.Tests;

/// <summary>
/// The Manga reader renders the shared reader frame (docs/UNIFIED_READER.md
/// "Reader frame"): bars, menus, contents panel and slider come from
/// reader-shell.js/css; manga-reader.js/css only render pages and settings.
/// </summary>
[TestClass]
public sealed class MangaReaderFrameTests
{
    [TestMethod]
    public void MangaReaderRendersTheSharedFrameWithWorkingControls()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Manga", "Read.cshtml");

        StringAssert.Contains(page, "data-reader-frame");
        StringAssert.Contains(page, "data-unified-reader");
        StringAssert.Contains(page, "data-manga-stage data-reader-surface");
        StringAssert.Contains(page, "data-reader-chrome-primary");
        StringAssert.Contains(page, "_ReaderIcon");
        StringAssert.Contains(page, "~/css/reader-shell.css");
        StringAssert.Contains(page, "~/js/reader-shell.js");
        StringAssert.Contains(page, "~/css/manga-reader.css");
        Assert.IsFalse(page.Contains("~/css/manga.css", StringComparison.Ordinal), "The reader no longer uses the library stylesheet.");
        StringAssert.Contains(page, "data-reader-progress-slider");
        StringAssert.Contains(page, "data-reader-progress-text");
        StringAssert.Contains(page, "data-reader-page-step=\"-1\"");
        StringAssert.Contains(page, "data-reader-page-step=\"1\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"chapters\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"bookmarks\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"notes\"");
        StringAssert.Contains(page, "data-reader-fullscreen-toggle");
        StringAssert.Contains(page, "manga-chapter-pill");

        foreach (var mode in new[] { "single", "double", "continuous", "horizontal", "webtoon" })
        {
            StringAssert.Contains(page, $"(\"{mode}\", ");
        }

        foreach (var setting in new[] { "zoom", "firstPageAlone", "rightToLeft", "autoNext", "sharpen", "crop", "gap", "fitWidth" })
        {
            StringAssert.Contains(page, $"data-manga-setting=\"{setting}\"");
        }

        // Every menu toggle points at a rendered menu and every contents opener at a tab.
        foreach (var name in Regex.Matches(page, "data-reader-menu-toggle=\"(?<name>[a-z]+)\"").Select(x => x.Groups["name"].Value).Distinct())
        {
            StringAssert.Contains(page, $"data-reader-menu=\"{name}\"", $"Menu '{name}' has no panel.");
        }

        foreach (var name in Regex.Matches(page, "data-reader-contents-open=\"(?<name>[a-z]+)\"").Select(x => x.Groups["name"].Value).Distinct())
        {
            StringAssert.Contains(page, $"data-reader-contents-tab=\"{name}\"", $"Contents tab '{name}' is missing.");
        }
    }

    [TestMethod]
    public void PageIndexIsBoundFromTheQueryNotTheRazorPageRouteValue()
    {
        // The route value "page" holds the Razor page path ("/Manga/Read").
        // Without [FromQuery] every page image and ?page= resume became page 0.
        foreach (var handler in new[] { nameof(ReadModel.OnGetAsync), nameof(ReadModel.OnGetPageAsync) })
        {
            var parameter = typeof(ReadModel).GetMethod(handler)!
                .GetParameters()
                .Single(x => x.Name == "page");
            var attribute = parameter.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.FromQueryAttribute), false)
                .Cast<Microsoft.AspNetCore.Mvc.FromQueryAttribute>()
                .SingleOrDefault();
            Assert.AreEqual("page", attribute?.Name, handler);
        }
    }

    [TestMethod]
    public void MangaReaderOffersNoTranslationControlsWithoutMangaTranslation()
    {
        // Jularr has no manga (speech bubble) translation yet: no language menu,
        // no translate button and no bubble settings may be rendered.
        var page = Read("src", "Jularr.Web", "Pages", "Manga", "Read.cshtml");
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "manga-reader.js");

        Assert.IsFalse(page.Contains("data-reader-menu=\"language\"", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("model=\"@(\"translate\")\"", StringComparison.Ordinal));
        Assert.IsFalse(Regex.IsMatch(page + script, "bubble|Sprechblase", RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public void MangaReaderKeepsProgressBookmarksAndNavigation()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Manga", "Read.cshtml");
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "manga-reader.js");
        var shell = Read("src", "Jularr.Web", "wwwroot", "js", "reader-shell.js");

        StringAssert.Contains(page, "asp-page-handler=\"Progress\"");
        StringAssert.Contains(page, "asp-page-handler=\"Bookmark\"");
        StringAssert.Contains(page, "asp-page-handler=\"RemoveBookmark\"");
        StringAssert.Contains(page, "asp-page-handler=\"Preference\"");
        StringAssert.Contains(page, "data-page=\"@Model.InitialPage\"");
        StringAssert.Contains(page, "?handler=Page&page=");

        StringAssert.Contains(script, "data.set(\"pageIndex\", String(page))");
        StringAssert.Contains(script, "\"pagehide\"");
        StringAssert.Contains(script, "jularr:reader-location");
        StringAssert.Contains(script, "jularr:reader-seek");
        StringAssert.Contains(script, "jularr:reader-page-edge");
        StringAssert.Contains(script, "jularr:reader-mode");
        StringAssert.Contains(script, "readerPanGesture");
        StringAssert.Contains(script, "prefers-reduced-motion");
        StringAssert.Contains(script, "direction === \"rtl\"");
        StringAssert.Contains(shell, "event.key === \"ArrowLeft\"");
        StringAssert.Contains(shell, "surface.addEventListener(\"pointerdown\"");
        StringAssert.Contains(shell, "pageDirection() === \"rtl\" ? -direction : direction");
        Assert.IsFalse(script.Contains("case \"ArrowLeft\"", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("stage?.addEventListener(\"touchend\"", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("manga-chrome-hidden", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MangaReaderTextComesFromTheCatalog()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "manga-reader.js");
        var keys = Regex.Matches(script, "\\bt\\(\\s*\"(?<key>[A-Za-z.]+)\"")
            .Select(x => x.Groups["key"].Value)
            .Distinct()
            .ToArray();

        Assert.IsTrue(keys.Length > 10, "The key scan found no catalog lookups.");
        foreach (var key in keys)
        {
            Assert.IsTrue(UiTranslationResources.TryGet("manga.reader." + key, out _), $"Missing catalog entry manga.reader.{key}.");
        }

        foreach (var german in new[] { "Lesezeichen", "Kapitel", "Seite" })
        {
            Assert.IsFalse(script.Contains(german, StringComparison.Ordinal), $"manga-reader.js hard-codes '{german}'.");
        }
    }

    [TestMethod]
    public void AppSidebarStaysVisibleInTheMangaReader()
    {
        foreach (var file in new[] { "manga.css", "manga-reader.css" })
        {
            var css = Read("src", "Jularr.Web", "wwwroot", "css", file);
            Assert.IsFalse(Regex.IsMatch(css, @"body:has\([^)]*\)\s*\.sidebar"), $"{file} hides the app sidebar.");
        }
    }

    [TestMethod]
    public void VolumesGroupConsecutiveChaptersAndMarkTheCurrentOne()
    {
        var seriesId = Guid.NewGuid();
        var chapters = new[]
        {
            Chapter(seriesId, 1, 1),
            Chapter(seriesId, 2, 1),
            Chapter(seriesId, 3, 2),
            Chapter(seriesId, 4, null),
            Chapter(seriesId, 5, null)
        };

        var volumes = ReadModel.GroupVolumes(chapters, chapters[2].Id);

        Assert.AreEqual(3, volumes.Count);
        Assert.AreEqual(1, volumes[0].Number);
        Assert.AreEqual(2, volumes[0].Chapters.Count);
        Assert.AreEqual(2, volumes[1].Number);
        Assert.IsTrue(volumes[1].ContainsCurrent);
        Assert.IsFalse(volumes[0].ContainsCurrent);
        Assert.IsNull(volumes[2].Number);
        Assert.AreEqual(4d, volumes[2].First.Number);
        Assert.AreEqual(5d, volumes[2].Last.Number);
        Assert.AreEqual(0, ReadModel.GroupVolumes([], Guid.NewGuid()).Count);
    }

    [TestMethod]
    public void ChapterTitleIsShownOnlyWhenItAddsInformation()
    {
        Assert.IsNull(ReadModel.DistinctTitle(12, "Chapter 12"));
        Assert.IsNull(ReadModel.DistinctTitle(12, "  "));
        Assert.IsNull(ReadModel.DistinctTitle(12.5, "chapter 12.5"));
        Assert.AreEqual("A New Beginning", ReadModel.DistinctTitle(12, " A New Beginning "));
    }

    private static MangaChapterItem Chapter(Guid seriesId, double number, int? volume) =>
        new(Guid.NewGuid(), seriesId, number, volume, $"Chapter {number}", 20, "archive", DateTime.UtcNow);

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

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

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
