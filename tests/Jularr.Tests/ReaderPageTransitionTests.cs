namespace Jularr.Tests;

[TestClass]
public sealed class ReaderPageTransitionTests
{
    [TestMethod]
    public void SharedShellOwnsPagedTransitionSelectionAndReducedMotion()
    {
        var shell = Read("src", "Jularr.Web", "wwwroot", "js", "reader-shell.js");
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "reader-shell.css");

        StringAssert.Contains(shell, "const pageTransitionKinds = new Set([\"curl\", \"slide\", \"fade\", \"none\"])");
        StringAssert.Contains(shell, "document.startViewTransition");
        StringAssert.Contains(shell, "prefers-reduced-motion: reduce");
        StringAssert.Contains(shell, "jularr:reader-rendered");
        StringAssert.Contains(shell, "root.dataset.readerTransitionActive = \"true\"");
        StringAssert.Contains(css, "::view-transition-old(jularr-reader-page)");
        StringAssert.Contains(css, "reader-shared-page-curl-out-next");
        StringAssert.Contains(css, "rotateY(-92deg)");
        StringAssert.Contains(css, "rotateY(92deg)");
    }

    [TestMethod]
    public void RenderersSignalCompletionWithoutOwningGenericTransitionKinds()
    {
        var book = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");
        var novel = Read("src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js");
        var pdf = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader-pdf.js");
        var manga = Read("src", "Jularr.Web", "wwwroot", "js", "manga-reader.js");

        foreach (var script in new[] { book, novel, pdf, manga })
        {
            StringAssert.Contains(script, "jularr:reader-rendered");
        }

        Assert.IsFalse(novel.Contains("const animatePage =", StringComparison.Ordinal));
        Assert.IsFalse(novel.Contains("reader-turn-curl-next", StringComparison.Ordinal));
        Assert.IsFalse(book.Contains("is-fading", StringComparison.Ordinal));
        Assert.IsFalse(pdf.Contains("is-turning-next", StringComparison.Ordinal));
        Assert.IsFalse(pdf.Contains("settings.pageTransition", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FeatureStylesDoNotRecreateGenericPageTransitions()
    {
        var bookCss = Read("src", "Jularr.Web", "wwwroot", "css", "book-reader.css");
        var novelCss = Read("src", "Jularr.Web", "wwwroot", "css", "novels.css");

        Assert.IsFalse(bookCss.Contains("book-pdf-turn-next", StringComparison.Ordinal));
        Assert.IsFalse(bookCss.Contains(".book-columns.is-fading", StringComparison.Ordinal));
        Assert.IsFalse(novelCss.Contains("reader-turn-curl-next", StringComparison.Ordinal));
        Assert.IsFalse(novelCss.Contains("reader-page-slide", StringComparison.Ordinal));
    }

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
