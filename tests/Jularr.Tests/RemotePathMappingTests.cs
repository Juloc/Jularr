using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Backup;
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
    public void LegacyGlobalMappingsMoveIntoEveryMediaTypeOnce()
    {
        var legacy = JsonSerializer.Deserialize<AnimeImportSettingsState>(
            """
            {"Version":1,"DefaultImportMode":0,"RootImportModes":{},
             "RemotePathMappings":[
               {"RemotePrefix":"/downloads","LocalPrefix":"/data/dl"},
               {"RemotePrefix":" ","LocalPrefix":"/ignored"}]}
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.IsTrue(RemotePathMappingMigration.IsNeeded(legacy));

        var migrated = RemotePathMappingMigration.Migrate(legacy);

        foreach (var kind in Enum.GetValues<MediaAcquisitionKind>())
        {
            Assert.AreEqual(
                "/data/dl/complete/x",
                migrated.TranslatePath(kind, "/downloads/complete/x"),
                $"{kind} translates exactly as the global list did.");
            Assert.AreEqual(1, migrated.RemotePathMappingsFor(kind).Count, "The blank legacy entry is dropped.");
        }

        Assert.IsFalse(RemotePathMappingMigration.IsNeeded(migrated));
        Assert.AreEqual(legacy.Version, migrated.Version, "The migration does not touch the settings version.");
        Assert.AreSame(migrated, RemotePathMappingMigration.Migrate(migrated), "A migrated state is left alone.");
    }

    [TestMethod]
    public void MigrationKeepsAnExistingPerMediaTypeMappingAndFolders()
    {
        var state = AnimeImportSettingsState.Empty() with
        {
            LegacyRemotePathMappings =
            [
                new RemotePathMapping("/downloads", "/legacy"),
                new RemotePathMapping("/tv", "/data/anime")
            ],
            MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
            {
                [MediaAcquisitionKind.Manga] = new("/data/media/manga", null, null)
                {
                    RemotePathMappings = [new RemotePathMapping("/DOWNLOADS", "/chosen")]
                }
            }
        };

        var migrated = RemotePathMappingMigration.Migrate(state);

        Assert.AreEqual("/chosen/x", migrated.TranslatePath(MediaAcquisitionKind.Manga, "/downloads/x"), "The owner's own per-type entry wins over the legacy copy.");
        Assert.AreEqual("/data/anime/y", migrated.TranslatePath(MediaAcquisitionKind.Manga, "/tv/y"));
        Assert.AreEqual("/data/media/manga", migrated.LibraryFor(MediaAcquisitionKind.Manga)!.LibraryRoot);
        Assert.AreEqual("/legacy/x", migrated.TranslatePath(MediaAcquisitionKind.Book, "/downloads/x"));
    }

    [TestMethod]
    public async Task StoreMigratesAndRewritesALegacyFileOnFirstRead()
    {
        using var directory = new TempDirectory();
        var path = WriteLegacyFile(
            directory.Path,
            """
            {"Version":1,"DefaultImportMode":1,"RootImportModes":{},
             "RemotePathMappings":[{"RemotePrefix":"/downloads/complete","LocalPrefix":"/data/downloads/complete"}],
             "MediaLibraries":{"Manga":{"LibraryRoot":"/data/media/manga","ImportMode":2,"InboxRoot":null}}}
            """);
        var store = new AnimeImportSettingsStore(directory.Path);

        var state = await store.LoadAsync();

        Assert.AreEqual(ImportMode.Copy, state.DefaultImportMode);
        Assert.AreEqual("/data/media/manga", state.LibraryFor(MediaAcquisitionKind.Manga)!.LibraryRoot);
        Assert.AreEqual(ImportMode.Hardlink, state.ModeFor(MediaAcquisitionKind.Manga));
        foreach (var kind in Enum.GetValues<MediaAcquisitionKind>())
        {
            Assert.AreEqual("/data/downloads/complete/job", state.TranslatePath(kind, "/downloads/complete/job"));
        }

        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.IsFalse(
            written.RootElement.TryGetProperty("remotePathMappings", out _),
            "The legacy global list is gone from the file.");
        var again = await new AnimeImportSettingsStore(directory.Path).LoadAsync();
        // The single legacy mapping is copied once into every media kind.
        Assert.AreEqual(Enum.GetValues<MediaAcquisitionKind>().Length, again.RemotePathMappingCount);
        Assert.IsFalse(RemotePathMappingMigration.IsNeeded(again));
    }

    [TestMethod]
    public async Task AnEmptyLegacyListIsRemovedWithoutCreatingMediaEntries()
    {
        using var directory = new TempDirectory();
        var path = WriteLegacyFile(
            directory.Path,
            """{"Version":1,"DefaultImportMode":0,"RootImportModes":{},"RemotePathMappings":[]}""");

        var state = await new AnimeImportSettingsStore(directory.Path).LoadAsync();

        Assert.AreEqual(0, state.MediaLibraries.Count);
        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.IsFalse(written.RootElement.TryGetProperty("remotePathMappings", out _));
    }

    [TestMethod]
    public async Task MappingMigrationLeavesTheMediaFolderMigrationItsTurn()
    {
        using var directory = new TempDirectory();
        WriteLegacyFile(
            directory.Path,
            """
            {"Version":1,"DefaultImportMode":0,"RootImportModes":{},
             "RemotePathMappings":[{"RemotePrefix":"/downloads","LocalPrefix":"/data/dl"}]}
            """);
        var store = new AnimeImportSettingsStore(directory.Path);

        // Any read migrates the mappings first (the settings page, the SABnzbd monitor).
        await store.LoadAsync();
        var moved = await MediaFolderSettingsMigration.MigrateAsync(
            store,
            "/data/inbox/books",
            legacyBooksSettingsPath: Path.Combine(directory.Path, "missing-integrations.json"));

        var state = await store.LoadAsync();
        Assert.IsTrue(moved, "The Books inbox still moves: mapping migration keeps the settings version.");
        Assert.AreEqual(MediaFolderSettingsMigration.MediaFoldersVersion, state.Version);
        Assert.AreEqual(Path.GetFullPath("/data/inbox/books"), state.InboxFor(MediaAcquisitionKind.Book));
        Assert.AreEqual("/data/dl/x", state.TranslatePath(MediaAcquisitionKind.Book, "/downloads/x"), "The inbox migration keeps the mappings of its media type.");
    }

    [TestMethod]
    public async Task ABackupTakenBeforePerMediaTypeMappingsRestoresIntoTheNewShape()
    {
        using var directory = new TempDirectory();
        var bundle = new AcquisitionBackupBundle(
            AcquisitionBackupService.CurrentVersion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["import-settings.json"] =
                    """{"Version":2,"DefaultImportMode":3,"RootImportModes":{},"RemotePathMappings":[{"RemotePrefix":"/tv","LocalPrefix":"/data/anime"}]}"""
            });

        var restored = await new AcquisitionBackupService(directory.Path).RestoreAsync(bundle, CancellationToken.None);

        Assert.IsTrue(restored.Success, string.Join(" ", restored.Errors));
        var state = await new AnimeImportSettingsStore(directory.Path).LoadAsync();
        Assert.AreEqual(ImportMode.HardlinkOrCopy, state.DefaultImportMode);
        Assert.AreEqual("/data/anime/Frieren", state.TranslatePath(MediaAcquisitionKind.Anime, "/tv/Frieren"));
        Assert.AreEqual("/data/anime/Frieren", state.TranslatePath(MediaAcquisitionKind.Book, "/tv/Frieren"));

        var exported = await new AcquisitionBackupService(directory.Path).ExportAsync(CancellationToken.None);
        using var document = JsonDocument.Parse(exported.Files["import-settings.json"]);
        Assert.IsTrue(
            document.RootElement.GetProperty("mediaLibraries").GetProperty("Anime").TryGetProperty("remotePathMappings", out var anime) &&
            anime.GetArrayLength() == 1,
            "A new backup carries the per-media-type mappings.");
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

    private static string WriteLegacyFile(string directory, string json)
    {
        var path = Path.Combine(directory, "acquisition", "import-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
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
