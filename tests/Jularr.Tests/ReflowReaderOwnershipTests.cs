namespace Jularr.Tests;

[TestClass]
public sealed class ReflowReaderOwnershipTests
{
    [TestMethod]
    public void CanonicalReflowRuntimeOwnsSharedNavigationMath()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "reflow-reader.js");

        StringAssert.Contains(script, "export const permilleForIndex");
        StringAssert.Contains(script, "export const indexForPermille");
        StringAssert.Contains(script, "export const scrollPermille");
        StringAssert.Contains(script, "export const scrollTopForPermille");
        StringAssert.Contains(script, "export const captureContinuousAnchor");
        StringAssert.Contains(script, "export const capturePagedRectAnchor");
        StringAssert.Contains(script, "export const capturePagedTextAnchor");
        StringAssert.Contains(script, "export const createReflowTextRenderer");
        StringAssert.Contains(script, "adapter.goToPage?.(pageIndex)");
        StringAssert.Contains(script, "adapter.scrollToPermille?.(");
        StringAssert.Contains(script, "adapter.captureAnchor?.(");
        StringAssert.Contains(script, "adapter.restoreAnchor?.(");
    }

    [TestMethod]
    public void ReflowRuntimeSupportsBothBookAndNovelParagraphContractsDuringMigration()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "reflow-reader.js");

        StringAssert.Contains(script, "paragraph?.dataset?.index ?? paragraph?.dataset?.bookParagraph");
        StringAssert.Contains(script, "paragraph?.dataset?.language || null");
    }

    [TestMethod]
    public void BookAndNovelUseTheSameReflowRuntime()
    {
        var book = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");
        var novel = Read("src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js");

        foreach (var script in new[] { book, novel })
        {
            StringAssert.Contains(script, "reflow-reader.js");
            StringAssert.Contains(script, "createReflowTextRenderer");
            StringAssert.Contains(script, "captureContinuousAnchor");
            StringAssert.Contains(script, "reflowRenderer?.turn(direction)");
            StringAssert.Contains(script, "reflowRenderer?.seek");
            Assert.IsFalse(script.Contains("const clamp =", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void SourceAdaptersDoNotBecomeSecondGenericInputOwners()
    {
        var book = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");
        var novel = Read("src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js");

        Assert.IsFalse(book.Contains("key === \"ArrowRight\"", StringComparison.Ordinal));
        Assert.IsFalse(book.Contains("key === \"PageDown\"", StringComparison.Ordinal));
        Assert.IsFalse(novel.Contains("event.key === \"ArrowRight\"", StringComparison.Ordinal));
        Assert.IsFalse(novel.Contains("event.key === \"PageDown\"", StringComparison.Ordinal));
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
