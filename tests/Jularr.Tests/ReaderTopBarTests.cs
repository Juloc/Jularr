using Jularr.Web.Features.ReaderCore;

namespace Jularr.Tests;

/// <summary>
/// The reader top bar of Books and Novels: a placeholder instead of a broken cover image,
/// the translation icon as the language control, and an immersive frame without the app sidebar.
/// </summary>
[TestClass]
public sealed class ReaderTopBarTests
{
    [TestMethod]
    [DataRow("Frieren: Beyond Journey's End", "F")]
    [DataRow("  sousou no frieren", "S")]
    [DataRow("葬送のフリーレン", "葬")]
    [DataRow("", "")]
    public void CoverPlaceholderShowsTheTitleInitial(string title, string expected)
    {
        Assert.AreEqual(expected, new ReaderCoverModel(title, null).Initial);
    }

    [TestMethod]
    public void ReadersUseTheCoverPartialAndTheTranslationIconForLanguage()
    {
        foreach (var page in new[] { "Books", "Novels" })
        {
            var markup = ReadRepositoryFile("src", "Jularr.Web", "Pages", page, "Read.cshtml");

            StringAssert.Contains(markup, "<partial name=\"_ReaderCover\"");
            Assert.IsFalse(markup.Contains("class=\"reader-frame-cover\"", StringComparison.Ordinal), $"{page}: raw cover image.");
            Assert.IsFalse(markup.Contains("⇄", StringComparison.Ordinal), $"{page}: language chip.");

            var languageToggle = markup[markup.IndexOf("data-reader-menu-toggle=\"language\"", StringComparison.Ordinal)..];
            languageToggle = languageToggle[..languageToggle.IndexOf("</button>", StringComparison.Ordinal)];
            StringAssert.Contains(languageToggle, "model=\"@(\"translate\")\"");
            StringAssert.Contains(languageToggle, "reader-frame-label");
        }
    }

    [TestMethod]
    public void ReaderFrameHidesTheAppSidebarAndFillsTheViewport()
    {
        var css = ReadRepositoryFile("src", "Jularr.Web", "wwwroot", "css", "reader-shell.css");

        StringAssert.Contains(css, "body:has([data-reader-frame]) .sidebar");
        StringAssert.Contains(css, "margin-inline-start: 0;");
        StringAssert.Contains(css, "min-height: 100dvh;");
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
