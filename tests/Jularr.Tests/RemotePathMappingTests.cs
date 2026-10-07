using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;

namespace Jularr.Tests;

/// <summary>
/// Remote path mappings belong to a media type (#389): the one <c>TranslatePath(kind, path)</c>
/// translates a path an external system reports for that media type, and the pre-per-media-type
/// global list is moved into every media type once.
/// </summary>
[TestClass]
public sealed class RemotePathMappingTests
{
    [TestMethod]
    public void MappingsOnlyApplyToTheirOwnMediaType()
    {
        var state = AnimeImportSettingsState.Empty()
            .WithRemotePathMappings(MediaAcquisitionKind.Manga, [new RemotePathMapping("/downloads", "/data/manga-in")])
            .WithRemotePathMappings(MediaAcquisitionKind.Book, [new RemotePathMapping("/downloads", "/data/books-in")]);

        Assert.AreEqual("/data/manga-in/complete/Frieren", state.TranslatePath(MediaAcquisitionKind.Manga, "/downloads/complete/Frieren"));
        Assert.AreEqual("/data/books-in/complete/Frieren", state.TranslatePath(MediaAcquisitionKind.Book, "/downloads/complete/Frieren"));
        Assert.AreEqual("/downloads/complete/Frieren", state.TranslatePath(MediaAcquisitionKind.Anime, "/downloads/complete/Frieren"), "Anime has no mapping and keeps the reported path.");
        Assert.AreEqual("/downloads/complete/Frieren", state.TranslatePath(MediaAcquisitionKind.LightNovel, "/downloads/complete/Frieren"));
    }

    [TestMethod]
    public void LongestMatchingPrefixWinsWithinAMediaType()
    {
        var state = AnimeImportSettingsState.Empty().WithRemotePathMappings(
            MediaAcquisitionKind.Anime,
            [
                new RemotePathMapping("/downloads", "/mnt/a"),
                new RemotePathMapping("/downloads/anime", "/mnt/b")
            ]);

        Assert.AreEqual("/mnt/b/Frieren", state.TranslatePath(MediaAcquisitionKind.Anime, "/downloads/anime/Frieren"));
        Assert.AreEqual("/mnt/a/other", state.TranslatePath(MediaAcquisitionKind.Anime, "/downloads/other"));
    }

    [TestMethod]
    public void PrefixesMatchWholeFoldersIgnoringCaseAndSeparatorStyle()
    {
        var state = AnimeImportSettingsState.Empty().WithRemotePathMappings(
            MediaAcquisitionKind.Book,
            [new RemotePathMapping(@"C:\Downloads\Complete\", "/data/dl")]);

        Assert.AreEqual("/data/dl/books/Atomic Habits", state.TranslatePath(MediaAcquisitionKind.Book, @"c:\downloads\complete\books\Atomic Habits"));
        Assert.AreEqual("/data/dl", state.TranslatePath(MediaAcquisitionKind.Book, @"C:\Downloads\Complete"), "The prefix folder itself maps to the local folder.");
        Assert.AreEqual(@"C:\Downloads\Complete-old\x", state.TranslatePath(MediaAcquisitionKind.Book, @"C:\Downloads\Complete-old\x"), "A longer folder name is not below the prefix.");
    }

    [TestMethod]
    public void ReplacingMappingsKeepsTheFoldersAndDropsAnEmptyMediaType()
    {
        var state = AnimeImportSettingsState.Empty() with
        {
            MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
            {
                [MediaAcquisitionKind.Manga] = new("/data/media/manga", ImportMode.Copy, "/data/inbox/manga")
            }
        };

        var mapped = state.WithRemotePathMappings(MediaAcquisitionKind.Manga, [new RemotePathMapping("/a", "/b")]);
        var target = mapped.FoldersFor(MediaAcquisitionKind.Manga);
        Assert.AreEqual("/data/media/manga", target.LibraryRoot);
        Assert.AreEqual(ImportMode.Copy, target.ImportMode);
        Assert.AreEqual("/data/inbox/manga", target.InboxRoot);
        Assert.AreEqual(1, mapped.RemotePathMappingCount);

        var anime = AnimeImportSettingsState.Empty().WithRemotePathMappings(MediaAcquisitionKind.Anime, [new RemotePathMapping("/a", "/b")]);
        Assert.AreEqual(1, anime.MediaLibraries.Count, "Anime keeps only its mappings in its media entry.");
        Assert.IsNull(anime.LibraryFor(MediaAcquisitionKind.Anime), "A mapping is not a library folder.");
        var cleared = anime.WithRemotePathMappings(MediaAcquisitionKind.Anime, []);
        Assert.AreEqual(0, cleared.MediaLibraries.Count);
        Assert.AreEqual(0, cleared.RemotePathMappingCount);
    }

    [TestMethod]
    public async Task PerMediaTypeMappingsSurviveSaveAndReload()
    {
        using var directory = new TempDirectory();
        var store = new AnimeImportSettingsStore(directory.Path);
        await store.UpdateAsync(state => state
            .WithRemotePathMappings(MediaAcquisitionKind.Anime, [new RemotePathMapping("/tv", "/data/anime")])
            .WithRemotePathMappings(MediaAcquisitionKind.Manga, [new RemotePathMapping("/dl", "/data/manga-dl")]));

        var reloaded = await new AnimeImportSettingsStore(directory.Path).LoadAsync();

        Assert.AreEqual("/data/anime/x", reloaded.TranslatePath(MediaAcquisitionKind.Anime, "/tv/x"));
        Assert.AreEqual("/data/manga-dl/x", reloaded.TranslatePath(MediaAcquisitionKind.Manga, "/dl/x"));
        Assert.AreEqual("/tv/x", reloaded.TranslatePath(MediaAcquisitionKind.Manga, "/tv/x"));
        Assert.AreEqual(2, reloaded.RemotePathMappingCount);
    }

    [TestMethod]
    public void ACategoryMapsBackToItsMediaType()
    {
        var settings = DownloadClientSettings.CreateDefault("http://sab.invalid");

        Assert.AreEqual(MediaAcquisitionKind.Manga, settings.KindForCategory("manga"));
        Assert.AreEqual(MediaAcquisitionKind.LightNovel, settings.KindForCategory(" LightNovels "));
        Assert.AreEqual(MediaAcquisitionKind.Movie, settings.KindForCategory("movies"));
        Assert.AreEqual(MediaAcquisitionKind.Tv, settings.KindForCategory("tv"));
        Assert.IsNull(settings.KindForCategory("not-jularr"), "A category no media type is mapped to belongs to no media type.");
        Assert.IsNull(settings.KindForCategory(null));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jularr-path-mapping-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
