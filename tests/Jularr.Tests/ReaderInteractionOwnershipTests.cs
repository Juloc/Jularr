namespace Jularr.Tests;

[TestClass]
public sealed class ReaderInteractionOwnershipTests
{
    [TestMethod]
    public void SharedShellOwnsGenericPageKeyboardSwipeTapAndChrome()
    {
        var root = RepositoryRoot();
        var shell = Read(root, "reader-shell.js");
        var book = Read(root, "books-reader.js");
        var novel = Read(root, "reader-personalization.js");
        var manga = Read(root, "manga-reader.js");
        var mangaPage = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Manga", "Read.cshtml"));

        StringAssert.Contains(shell, "const dispatchPhysicalPage = direction =>");
        StringAssert.Contains(shell, "pageDirection() === \"rtl\" ? -direction : direction");
        StringAssert.Contains(shell, "event.key === \"ArrowRight\"");
        StringAssert.Contains(shell, "event.key === \"ArrowLeft\"");
        StringAssert.Contains(shell, "event.key === \"PageDown\"");
        StringAssert.Contains(shell, "event.key === \"PageUp\"");
        StringAssert.Contains(shell, "surface.addEventListener(\"pointerdown\"");
        StringAssert.Contains(shell, "surface.addEventListener(\"pointerup\"");
        StringAssert.Contains(shell, "jularr:reader-mode");
        StringAssert.Contains(shell, "jularr:reader-chrome");
        StringAssert.Contains(shell, "root.dataset.readerImmersive");
        StringAssert.Contains(shell, "root.dataset.readerPanGesture");

        StringAssert.Contains(book, "jularr:reader-page-edge");
        StringAssert.Contains(book, "jularr:reader-seek");
        Assert.IsFalse(book.Contains("key === \"ArrowRight\"", StringComparison.Ordinal));
        Assert.IsFalse(book.Contains("key === \"PageDown\"", StringComparison.Ordinal));
        Assert.IsFalse(book.Contains("handlePdfKey", StringComparison.Ordinal));
        Assert.IsFalse(book.Contains("root.dataset.readingMode =", StringComparison.Ordinal));

        StringAssert.Contains(novel, "jularr:reader-page-edge");
        Assert.IsFalse(novel.Contains(
            "if (state.readingMode !== \"paged\") return;\n        if (event.target.matches",
            StringComparison.Ordinal));
        Assert.IsFalse(novel.Contains(
            "querySelector(\"[data-reader-page-prev]\")?.addEventListener",
            StringComparison.Ordinal));
        Assert.IsFalse(novel.Contains("shell.dataset.readingMode =", StringComparison.Ordinal));

        StringAssert.Contains(mangaPage, "data-manga-stage data-reader-surface");
        StringAssert.Contains(manga, "jularr:reader-page-edge");
        StringAssert.Contains(manga, "jularr:reader-mode");
        StringAssert.Contains(manga, "jularr:reader-chrome");
        StringAssert.Contains(manga, "jularr:reader-bookmark");
        StringAssert.Contains(manga, "readerPanGesture");
        Assert.IsFalse(manga.Contains("case \"ArrowLeft\"", StringComparison.Ordinal));
        Assert.IsFalse(manga.Contains("stage?.addEventListener(\"touchend\"", StringComparison.Ordinal));
        Assert.IsFalse(manga.Contains("stage?.addEventListener(\"click\"", StringComparison.Ordinal));
        Assert.IsFalse(manga.Contains("manga-chrome-hidden", StringComparison.Ordinal));
        Assert.IsFalse(manga.Contains("root.dataset.readingMode =", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RendererSpecificDragCannotAlsoReachTheSharedPointerTurn()
    {
        var root = RepositoryRoot();
        var book = Read(root, "books-reader.js");

        StringAssert.Contains(book, "const dragEnabled = () =>");
        StringAssert.Contains(book, "event.stopPropagation()");
        StringAssert.Contains(book, "goToView(currentView + direction)");
    }

    private static string Read(string root, string fileName) =>
        File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", fileName));

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
