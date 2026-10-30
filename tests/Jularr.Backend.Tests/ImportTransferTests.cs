using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.Storage;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ImportTransferTests
    {
        [TestMethod]
        public async Task AllNinetyNineNamesAreTaken_DoesNotClaimAnOccupiedTarget()
        {
            using var directory = new TemporaryDirectory();
            for (var attempt = 1; attempt <= 99; attempt++)
            {
                File.WriteAllText(Path.Combine(directory.Path, LibraryNaming.Alternative("film", attempt) + ".mkv"), "existing");
            }

            var result = await DownloadImport.FreeStemAsync(new LocalStorageFiles(directory.Path), "", "film", [".mkv"], new HashSet<string>(), CancellationToken.None);

            Assert.IsNull(result);
            Assert.AreEqual("existing", File.ReadAllText(Path.Combine(directory.Path, "film (99).mkv")));
        }

        [TestMethod]
        public async Task ImportNameReservation_ChecksEveryExtensionInAGroupAndTheInventory()
        {
            using var directory = new TemporaryDirectory();
            File.WriteAllText(Path.Combine(directory.Path, "film.mkv"), "existing");
            File.WriteAllText(Path.Combine(directory.Path, "film (2).en.srt"), "existing");

            // A name the inventory still gives an assigned file that went missing is taken too.
            var result = await DownloadImport.FreeStemAsync(new LocalStorageFiles(directory.Path), "", "film", [".mkv", ".en.srt"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FILM (3).mkv" },
                CancellationToken.None);

            Assert.AreEqual("film (4)", result);
        }

        [TestMethod]
        public async Task InterruptedCopy_RemovesTemporaryFileAndRetainsOriginal()
        {
            using var directory = new TemporaryDirectory();
            var source = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(directory.Path, "library")).FullName;
            var bytes = new byte[] { 10, 20, 30, 40, 50 };
            await File.WriteAllBytesAsync(Path.Combine(source, "film.mkv"), bytes);

            // The reservation hook forces the safe-copy path even on one filesystem.
            await Assert.ThrowsAsync<IOException>(() => StorageFileTransfer.PlaceAsync(
                new LocalStorageFiles(source), "film.mkv", new LocalStorageFiles(target), "Movie/film.mkv",
                CancellationToken.None, _ => throw new IOException("Simulated storage fault")));

            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(source, "film.mkv")));
            Assert.IsFalse(File.Exists(Path.Combine(target, "Movie", "film.mkv")));
            Assert.AreEqual(0, Directory.EnumerateFiles(target, "*.jularr-partial", SearchOption.AllDirectories).Count());
        }

        [TestMethod]
        public async Task ExistingLibraryFile_IsNotOverwrittenAndDownloadRemains()
        {
            using var directory = new TemporaryDirectory();
            var source = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(directory.Path, "library")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "film.mkv"), "new release");
            await File.WriteAllTextAsync(Path.Combine(target, "film.mkv"), "existing release");

            await Assert.ThrowsAsync<IOException>(() => StorageFileTransfer.PlaceAsync(
                new LocalStorageFiles(source), "film.mkv", new LocalStorageFiles(target), "film.mkv", CancellationToken.None));

            Assert.AreEqual("existing release", await File.ReadAllTextAsync(Path.Combine(target, "film.mkv")));
            Assert.AreEqual("new release", await File.ReadAllTextAsync(Path.Combine(source, "film.mkv")));
            Assert.AreEqual(0, Directory.EnumerateFiles(target, "*.jularr-partial", SearchOption.AllDirectories).Count());
        }

        [TestMethod]
        public async Task SuccessfulCopy_PublishesCompleteFileAndRetainsDownload()
        {
            using var directory = new TemporaryDirectory();
            var source = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(directory.Path, "library")).FullName;
            var bytes = new byte[] { 11, 22, 33, 44, 55 };
            await File.WriteAllBytesAsync(Path.Combine(source, "book.epub"), bytes);

            var result = await StorageFileTransfer.PlaceAsync(
                new LocalStorageFiles(source), "book.epub", new LocalStorageFiles(target), "Books/book.epub",
                CancellationToken.None, _ => new NoopReservation());

            Assert.AreEqual((long)bytes.Length, result.Length);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(target, "Books", "book.epub")));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(source, "book.epub")));
            Assert.AreEqual(0, Directory.EnumerateFiles(target, "*.jularr-partial", SearchOption.AllDirectories).Count());
        }

        private sealed class NoopReservation : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
