using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The audiobook (#440) completed-download and inbox importers place audio files into the audiobook
/// library with the right naming and record a first-class <see cref="Audiobook"/> bridged to the media
/// core as a Book work with an <c>audiobook</c> edition/version.
/// </summary>
[TestClass]
public sealed class AudiobookImportTests
{
    private static MediaAcquisitionRegistry Registry() =>
        new([new AudiobookAcquisitionRegistration()]);

    private static async Task<AnimeImportSettingsStore> SettingsWithLibraryAsync(AppDbContext db, string dataRoot, string libraryRoot)
    {
        await ReadingTestRoots.AssignAsync(db, MediaAcquisitionKind.Audiobook, libraryRoot, ImportMode.Copy);
        return new AnimeImportSettingsStore(dataRoot);
    }

    private static AudiobookCompletedDownloadImportAdapter Adapter(
        AppDbContext db, AnimeImportSettingsStore settings) =>
        new(
            new AudiobookLibraryService(db, Bridge(db)),
            Registry(),
            settings,
            new FileSystemHardLinkCreator(),
            NullLogger<AudiobookCompletedDownloadImportAdapter>.Instance,
            new Jularr.Web.Features.Storage.LibraryRootRoutingService(db));

    [TestMethod]
    public async Task M4bImportPlacesFileAndBridgesToBookWorkWithAudiobookEdition()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, "Dune.2021.Unabridged.M4B-GROUP.m4b"), "audio");
        var library = temp.Dir("library");
        var settings = await SettingsWithLibraryAsync(db, temp.Root, library);

        var result = await Adapter(db, settings).ImportAsync(
            new CompletedDownloadImportRequest(null, null, download, MediaAcquisitionKind.Audiobook), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);

        var audiobook = await db.Audiobooks.SingleAsync();
        Assert.AreEqual(2021, audiobook.Year);
        Assert.IsFalse(string.IsNullOrWhiteSpace(audiobook.Title));

        var placed = Directory.GetFiles(library, "*.m4b", SearchOption.AllDirectories);
        Assert.AreEqual(1, placed.Length);

        var file = await db.AudiobookFiles.SingleAsync();
        Assert.AreEqual(AudiobookFileFormats.M4b, file.Format);
        Assert.AreEqual(audiobook.Id, file.AudiobookId);

        // Bridged to the media core as a Book work with an audiobook edition owning an audiobook version.
        var work = await db.Works.SingleAsync();
        Assert.AreEqual(WorkMediaType.Book, work.MediaType);
        Assert.AreEqual(
            1,
            await db.WorkSourceLinks.CountAsync(x => x.SourceKind == WorkSourceKind.Audiobook && x.SourceId == audiobook.Id));

        var edition = await db.WorkEditions.SingleAsync(x => x.WorkId == work.Id);
        Assert.AreEqual(LegacyWorkBridge.AudiobookEditionFormat, edition.Format);
        var version = await db.WorkVersions.SingleAsync(x => x.WorkId == work.Id);
        Assert.AreEqual(edition.Id, version.EditionId);
    }

    [TestMethod]
    public async Task MultiFileAudiobookImportsEveryPartAndCountsChapters()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var download = temp.Dir(Path.Combine("download", "The Hobbit (1937)"));
        File.WriteAllText(Path.Combine(download, "The Hobbit - Part 01.mp3"), "audio");
        File.WriteAllText(Path.Combine(download, "The Hobbit - Part 02.mp3"), "audio");
        File.WriteAllText(Path.Combine(download, "The Hobbit - Part 03.mp3"), "audio");
        var library = temp.Dir("library");
        var settings = await SettingsWithLibraryAsync(db, temp.Root, library);

        var result = await Adapter(db, settings).ImportAsync(
            new CompletedDownloadImportRequest(null, null, download, MediaAcquisitionKind.Audiobook), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);

        var audiobook = await db.Audiobooks.SingleAsync();
        Assert.AreEqual(3, audiobook.ChapterCount);
        Assert.AreEqual(3, await db.AudiobookFiles.CountAsync(x => x.AudiobookId == audiobook.Id));
        Assert.AreEqual(3, Directory.GetFiles(library, "*.mp3", SearchOption.AllDirectories).Length);

        // One Book work, one audiobook edition — a multi-file audiobook is still a single edition/version.
        Assert.AreEqual(1, await db.Works.CountAsync());
        Assert.AreEqual(1, await db.WorkEditions.CountAsync());
    }

    [TestMethod]
    public async Task InboxImportsAFolderOfPartsAsOneAudiobook()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var inbox = temp.Dir("inbox");
        var bookFolder = Path.Combine(inbox, "Frankenstein (1818)");
        Directory.CreateDirectory(bookFolder);
        File.WriteAllText(Path.Combine(bookFolder, "chapter-1.mp3"), "audio");
        File.WriteAllText(Path.Combine(bookFolder, "chapter-2.mp3"), "audio");
        var settings = new AnimeImportSettingsStore(temp.Root);

        var result = await Adapter(db, settings).ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(1, result.Imported);
        var audiobook = await db.Audiobooks.SingleAsync();
        Assert.AreEqual(2, await db.AudiobookFiles.CountAsync(x => x.AudiobookId == audiobook.Id));
        Assert.AreEqual(WorkMediaType.Book, (await db.Works.SingleAsync()).MediaType);
    }

    [TestMethod]
    public async Task DownloadWithoutAudioIsRejected()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, "cover.jpg"), "image");
        var settings = new AnimeImportSettingsStore(temp.Root);

        var result = await Adapter(db, settings).ImportAsync(
            new CompletedDownloadImportRequest(null, null, download, MediaAcquisitionKind.Audiobook), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, result.Disposition);
        Assert.AreEqual(0, await db.Audiobooks.CountAsync());
    }

    private static LegacyWorkBridge Bridge(AppDbContext db) =>
        new(db, new WorkService(db), new WorkStructureService(db));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"jularr-audiobook-{Guid.NewGuid():N}");

        public TempWorkspace() => Directory.CreateDirectory(Root);

        public string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp workspace.
            }
        }
    }
}
