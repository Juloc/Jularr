namespace Jularr.Tests;

[TestClass]
public sealed class FixedPageReaderOwnershipTests
{
    [TestMethod]
    public void CanonicalFixedRuntimeOwnsPageMappingSpreadAndNavigationState()
    {
        var runtime = Read("fixed-page-reader.js");

        StringAssert.Contains(runtime, "const createPageMap =");
        StringAssert.Contains(runtime, "const createSpreadProjection =");
        StringAssert.Contains(runtime, "const createState =");
        StringAssert.Contains(runtime, "const targetForTurn = direction =>");
        StringAssert.Contains(runtime, "setPageCount");
        StringAssert.Contains(runtime, "setLayout");
        StringAssert.Contains(runtime, "setView");
    }

    [TestMethod]
    public void PdfAdapterUsesFixedRuntimeWithoutCopyingGenericProjectionMath()
    {
        var adapter = Read("books-reader-pdf.js");
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Read.cshtml"));

        StringAssert.Contains(page, "~/js/fixed-page-reader.js");
        StringAssert.Contains(adapter, "const fixedPage = window.JularrFixedPage;");
        StringAssert.Contains(adapter, "fixedPage.createPageMap");
        StringAssert.Contains(adapter, "fixedPage.createSpreadProjection");
        StringAssert.Contains(adapter, "fixedPage.createState");
        StringAssert.Contains(adapter, "fixedState.targetForTurn(direction)");
        StringAssert.Contains(adapter, "fixedState.setLayout(nextMode, nextPerView)");
        Assert.IsFalse(adapter.Contains("Math.floor(pageCount / 2) + 1", StringComparison.Ordinal), "Spread projection belongs to fixed-page-reader.js.");
        Assert.IsFalse(adapter.Contains("const chapters = Math.max(1, pageChapters.length)", StringComparison.Ordinal), "Physical-page locator mapping belongs to fixed-page-reader.js.");
    }

    [TestMethod]
    public void PdfSpecificRenderingRemainsInThePdfAdapter()
    {
        var adapter = Read("books-reader-pdf.js");
        var runtime = Read("fixed-page-reader.js");

        StringAssert.Contains(adapter, "lib.getDocument");
        StringAssert.Contains(adapter, "MAX_CANVAS_PIXELS");
        StringAssert.Contains(adapter, "textLayer");
        StringAssert.Contains(adapter, "KEEP_RENDERED");
        Assert.IsFalse(runtime.Contains("pdfjs", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(runtime.Contains("canvas", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(runtime.Contains("textLayer", StringComparison.Ordinal));
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", fileName));

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
