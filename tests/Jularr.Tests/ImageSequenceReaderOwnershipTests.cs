namespace Jularr.Tests;

[TestClass]
public sealed class ImageSequenceReaderOwnershipTests
{
    [TestMethod]
    public void CanonicalImageRuntimeOwnsPageSpreadAndMoveSemantics()
    {
        var script = Read("image-sequence-reader.js");

        StringAssert.Contains(script, "export const ImageSequenceMode");
        StringAssert.Contains(script, "export const spreadStartFor");
        StringAssert.Contains(script, "export const spreadPagesFor");
        StringAssert.Contains(script, "export const createImageSequenceRenderer");
        StringAssert.Contains(script, "const targetForMove = delta =>");
        StringAssert.Contains(script, "get isPaged() { return isPagedImageMode(mode); }");
        StringAssert.Contains(script, "setFirstPageAlone");
        StringAssert.Contains(script, "setDirection");
    }

    [TestMethod]
    public void MangaUsesCanonicalImageRuntimeInsteadOfCopyingNavigation()
    {
        var script = Read("manga-reader.js");

        StringAssert.Contains(script, "image-sequence-reader.js");
        StringAssert.Contains(script, "createImageSequenceRenderer");
        StringAssert.Contains(script, "imageRenderer.targetForMove(1)");
        StringAssert.Contains(script, "imageRenderer.targetForMove(-1)");
        StringAssert.Contains(script, "imageRenderer.setPage(target)");
        StringAssert.Contains(script, "imageRenderer.setMode(next)");
        StringAssert.Contains(script, "imageRenderer.setDirection");
        StringAssert.Contains(script, "imageRenderer.setFirstPageAlone");
        Assert.IsFalse(
            script.Contains("if (mode !== \"double\") return index;", StringComparison.Ordinal),
            "Spread projection belongs to image-sequence-reader.js.");
        Assert.IsFalse(
            script.Contains("localStorage.", StringComparison.Ordinal),
            "Image reader settings have one durable ReaderPreference owner.");
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Jularr.Web",
            "wwwroot",
            "js",
            fileName));

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
