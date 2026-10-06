using System.Text.RegularExpressions;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderThemes;

namespace Jularr.Tests;

/// <summary>
/// Light Novel UI design pass: chapter titles are not drawn twice, paged mode has no empty
/// last page, the page is paper like the Books reader and the theme catalog answers fast.
/// </summary>
[TestClass]
public sealed class NovelDesignPassTests
{
    [TestMethod]
    [DataRow("Chapter 3", true)]
    [DataRow("chapter IV", true)]
    [DataRow("Kapitel 12", true)]
    [DataRow("第三章", true)]
    [DataRow("第12話", true)]
    [DataRow("7", true)]
    [DataRow("Letter 1", false)]
    [DataRow("Eine neue Welt", false)]
    [DataRow("プロローグ", false)]
    [DataRow("", false)]
    public void NumberingTitlesAreRecognised(string title, bool expected) =>
        Assert.AreEqual(expected, NovelTextLayout.IsNumberingTitle(title));

    [TestMethod]
    public void TitleEchoIgnoresSpacingCaseAndTrailingPunctuation()
    {
        Assert.IsTrue(NovelTextLayout.IsTitleEcho("Letter 1", "Letter 1"));
        Assert.IsTrue(NovelTextLayout.IsTitleEcho("LETTER  1.", "Letter 1"));
        Assert.IsTrue(NovelTextLayout.IsTitleEcho("第一章　", "第一章"));
        Assert.IsFalse(NovelTextLayout.IsTitleEcho("To Mrs. Saville, England.", "Letter 1"));
        Assert.IsFalse(NovelTextLayout.IsTitleEcho("Letter 1", null));
    }

    [TestMethod]
    public void ReaderDrawsTheTitleOnceAndKeepsTheEchoInTheDom()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Novels", "Read.cshtml");
        var frameCss = Read("src", "Jularr.Web", "wwwroot", "css", "novel-reader-frame.css");
        var drawer = Read("src", "Jularr.Web", "wwwroot", "js", "novel-chapter-drawer.js");

        StringAssert.Contains(page, "NovelTextLayout.IsNumberingTitle(chapter.Title)");
        StringAssert.Contains(page, "is-title-echo");
        Assert.IsFalse(
            page.Contains("reader-progress-label", StringComparison.Ordinal),
            "The bottom bar must not repeat the chapter label of the top bar.");
        StringAssert.Contains(frameCss, ".novel-reader-segment.is-title-echo");
        StringAssert.Contains(drawer, "numberingPattern.test(titleText)");
    }

    [TestMethod]
    public void PagedColumnGapIsTwiceTheInlinePadding()
    {
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "novels.css");
        var paged = Regex.Match(
            css,
            @"\.novel-reader-shell\[data-reading-mode=""paged""\] \.novel-reader-content \{(?<body>[^}]*)\}");

        Assert.IsTrue(paged.Success);
        StringAssert.Contains(paged.Groups["body"].Value, "padding: var(--novel-page-pad-y) var(--novel-page-pad-x);");
        StringAssert.Contains(paged.Groups["body"].Value, "column-gap: calc(var(--novel-page-pad-x) * 2);");
        Assert.IsFalse(
            Regex.IsMatch(css, @"column-gap:(?>\s*)(?!calc\(var\(--novel-page-pad-x\) \* 2\))[^;]+;"),
            "Any other paged column gap makes the page count include an empty last page.");

        var script = Read("src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js");
        // The page count is the shared ceil((scrollWidth - 2) / columnStride) measurement with one page width as the stride.
        StringAssert.Contains(script, "const width = Math.max(1, content.clientWidth);");
        StringAssert.Contains(script, "columnStride: width,");
        StringAssert.Contains(script, "trailingCompensation: 2");
        var reflow = Read("src", "Jularr.Web", "wwwroot", "js", "reflow-reader.js");
        StringAssert.Contains(reflow, "Number(scrollWidth)");
        StringAssert.Contains(reflow, "- Number(trailingCompensation || 0)");
        StringAssert.Contains(reflow, "Math.ceil(rawPages)");
    }

    [TestMethod]
    public void PagedPageIsPaperWithArtOnlyInTheBottomMargin()
    {
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "novel-reader-frame.css");

        StringAssert.Contains(css, "url(\"/brand/washi.webp\")");
        StringAssert.Contains(css, "url(\"/brand/ink-mountains.svg\")");
        StringAssert.Contains(css, "padding-bottom: var(--novel-page-art-h);");
        StringAssert.Contains(css, "100% var(--novel-page-art-h)");
    }

    [TestMethod]
    public void ThemeCatalogIsScannedOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-theme-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "fantasy"));
        File.WriteAllBytes(Path.Combine(root, "fantasy", "castle.webp"), [0]);
        try
        {
            var catalog = new ReaderThemeCatalog(root);
            var first = catalog.GetAll();
            File.WriteAllBytes(Path.Combine(root, "fantasy", "forest.webp"), [0]);

            Assert.AreSame(first, catalog.GetAll());
            Assert.AreEqual(first.Count, catalog.GetAll().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EpubPickersUseTheLocalizedButtonInsteadOfTheBrowserText()
    {
        var index = Read("src", "Jularr.Web", "Pages", "Novels", "Index.cshtml");
        var work = Read("src", "Jularr.Web", "Pages", "Novels", "Work.cshtml");

        foreach (var page in new[] { index, work })
        {
            StringAssert.Contains(page, "data-novel-file-picker");
            StringAssert.Contains(page, "novels.index.chooseEpubs");
            StringAssert.Contains(page, "~/js/novel-file-picker.js");
        }

        // Search stays the panel's primary action; import and upload are secondary.
        StringAssert.Contains(index, "<button class=\"button\" type=\"submit\">@ui[\"novels.index.importSubmit\"]</button>");
        StringAssert.Contains(index, "<button class=\"button\" type=\"submit\">@ui[\"novels.index.uploadSubmit\"]</button>");
    }

    [TestMethod]
    public void ContentsViewsRenderChapterGroupsCollapsibleAndWithoutAnEyebrow()
    {
        var work = Read("src", "Jularr.Web", "Pages", "Novels", "Work.cshtml");
        var volumeGroup = Read("src", "Jularr.Web", "Pages", "Novels", "_NovelVolumeGroup.cshtml");
        var row = Read("src", "Jularr.Web", "Pages", "Novels", "_NovelChapterRow.cshtml");
        var drawer = Read("src", "Jularr.Web", "wwwroot", "js", "novel-chapter-drawer.js");
        var libraryCss = Read("src", "Jularr.Web", "wwwroot", "css", "novel-library.css");
        var frameCss = Read("src", "Jularr.Web", "wwwroot", "css", "novel-reader-frame.css");

        // The work page owns presentation-group selection and delegates chapter rendering
        // to the canonical volume-group partial for both grouped and ungrouped layouts.
        StringAssert.Contains(work, "Model.PresentationVolumeSections.Count > 0");
        StringAssert.Contains(work, "Model.PresentationChapterSections.Count > 0");
        StringAssert.Contains(work, "<details class=\"presentation-group\" open>");
        StringAssert.Contains(work, "<partial name=\"_NovelVolumeGroup\"");

        // The volume-group partial remains the single owner of chapter-run grouping.
        StringAssert.Contains(volumeGroup, "GroupRuns(Model.Chapters)");
        StringAssert.Contains(volumeGroup, "<details class=\"novel-chapter-group\" open>");
        StringAssert.Contains(volumeGroup, "<summary class=\"novel-chapter-group-heading\">@run.GroupTitle</summary>");
        Assert.AreEqual(2, CountOccurrences(volumeGroup, "<partial name=\"_NovelChapterRow\""));
        StringAssert.Contains(row, "novel-chapter-row");

        // Reader drawer: a group heading only appears when the loaded window carries a
        // groupTitle, and toggles its own rows via a plain data attribute (no eyebrow text).
        StringAssert.Contains(drawer, "item.groupTitle");
        StringAssert.Contains(drawer, "novel-drawer-group-heading");
        StringAssert.Contains(drawer, "data-group-heading");
        StringAssert.Contains(drawer, "aria-expanded");

        StringAssert.Contains(libraryCss, ".novel-chapter-group-heading");
        StringAssert.Contains(frameCss, ".novel-drawer-group-heading");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
