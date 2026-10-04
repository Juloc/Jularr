namespace Jularr.Tests;

[TestClass]
public sealed class ReaderArchitectureGuardTests
{
    [TestMethod]
    public void CanonicalReaderContractsStaySourceAndPersistenceAgnostic()
    {
        var root = FindRepositoryRoot();
        var readerCore = Path.Combine(root, "src", "Jularr.Web", "Features", "ReaderCore");
        var contractFiles = new[]
        {
            Path.Combine(readerCore, "ReaderDocument.cs"),
            Path.Combine(readerCore, "ReaderContent.cs"),
            Path.Combine(readerCore, "ReaderLocator.cs"),
            Path.Combine(root, "src", "Jularr.Web", "Features", "Books", "BookReaderDocumentAdapter.cs"),
            Path.Combine(root, "src", "Jularr.Web", "Features", "Novels", "NovelReaderDocumentAdapter.cs"),
            Path.Combine(root, "src", "Jularr.Web", "Features", "Manga", "MangaReaderDocumentAdapter.cs")
        };

        foreach (var file in contractFiles)
        {
            var source = File.ReadAllText(file);
            Assert.IsFalse(source.Contains("NovelProgress", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not own Novel progress.");
            Assert.IsFalse(source.Contains("MangaProgress", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not own Manga progress.");
            Assert.IsFalse(source.Contains("BookProgress", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not create a Book progress owner.");
            Assert.IsFalse(source.Contains("PositionMs", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not reuse video time as a Reader locator.");
            Assert.IsFalse(source.Contains("localStorage", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not own browser preference persistence.");
            Assert.IsFalse(source.Contains("/Books/", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not infer behavior from the Books route.");
            Assert.IsFalse(source.Contains("/Novels/", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not infer behavior from the Novels route.");
            Assert.IsFalse(source.Contains("/Manga/", StringComparison.Ordinal), $"{Path.GetFileName(file)} must not infer behavior from the Manga route.");
        }
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

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
