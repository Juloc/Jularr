using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReadingFilesTests
    {
        [TestMethod]
        public async Task Files_KeepTypeAndContentTag_AndAreReplacedByTheirPath()
        {
            using var folder = new TemporaryDirectory();
            var files = new ReadingFiles(Options.Create(new MediaOptions { ReadingCachePath = folder.Path }), TimeProvider.System);

            // Any path a book names, with its type and a tag of its content.
            var written = await files.WriteAsync(7, "OEBPS/images/ä b?.svg", "image/svg+xml", "<svg/>"u8.ToArray(), default);
            var found = files.Find(7, "OEBPS/images/ä b?.svg")!;
            Assert.AreEqual(("image/svg+xml", written!.ETag), (found.MediaType, found.ETag));
            CollectionAssert.AreEqual("<svg/>"u8.ToArray(), File.ReadAllBytes(found.FullPath));
            Assert.IsNull(files.Find(7, "OEBPS/images/other.svg"));
            Assert.IsNull(files.Find(8, "OEBPS/images/ä b?.svg"));

            // New bytes under the same path replace the old ones; a type that is no plain media type is not kept.
            var replaced = await files.WriteAsync(7, "OEBPS/images/ä b?.svg", "image/png", new byte[] { 1, 2, 3 }, default);
            Assert.AreNotEqual(written.ETag, replaced!.ETag);
            Assert.AreEqual("image/png", files.Find(7, "OEBPS/images/ä b?.svg")!.MediaType);
            Assert.HasCount(1, Directory.GetFiles(files.Folder(7)));
            Assert.IsNull(await files.WriteAsync(7, "x", "image/png; charset=../../x", new byte[] { 1 }, default));

            // A book is complete once every picture it draws is kept; it leaves as a whole.
            Assert.IsFalse(files.IsComplete(7));
            files.Complete(7);
            Assert.IsTrue(files.IsComplete(7));
            files.Delete([7, 9]);
            Assert.IsFalse(Directory.Exists(files.Folder(7)));
            Assert.IsFalse(files.IsComplete(7));
        }

        [TestMethod]
        public async Task Trim_RemovesTheBooksUsedLongestAgo_ButNeverOneInUse()
        {
            using var folder = new TemporaryDirectory();
            var files = new ReadingFiles(Options.Create(new MediaOptions { ReadingCachePath = folder.Path }), TimeProvider.System);
            for (var document = 1L; document <= 4; document++)
            {
                await files.WriteAsync(document, "pdf/smart/00001.jpg", "image/jpeg", new byte[1000], default);
                files.Complete(document);
            }

            // Book 1 was read an hour ago, book 2 two hours ago, book 3 three; book 4 is being read.
            for (var document = 1L; document <= 3; document++)
            {
                Directory.SetLastWriteTimeUtc(files.Folder(document), DateTime.UtcNow.AddHours(-document));
            }

            // Within the budget nothing leaves.
            files.Trim(4000);
            Assert.AreEqual(4, Directory.GetDirectories(folder.Path).Length);

            // Over it the oldest leave first, until the rest fits.
            files.Trim(2500);
            CollectionAssert.AreEquivalent(new[] { "1", "4" }, Directory.GetDirectories(folder.Path).Select(Path.GetFileName).ToArray());

            // Reading a book again puts it first; a book in use stays even beyond the budget.
            Assert.IsNotNull(files.Find(1, "pdf/smart/00001.jpg"));
            Directory.SetLastWriteTimeUtc(files.Folder(4), DateTime.UtcNow.AddHours(-1));
            files.Trim(1000);
            CollectionAssert.AreEquivalent(new[] { "1" }, Directory.GetDirectories(folder.Path).Select(Path.GetFileName).ToArray());
            files.Trim(0);
            Assert.IsTrue(files.IsComplete(1));

            // Once it is no longer in use it leaves too; a book missing on disk is drawn again by the reader.
            Directory.SetLastWriteTimeUtc(files.Folder(1), DateTime.UtcNow - ReadingFiles.InUse - TimeSpan.FromMinutes(1));
            await files.WriteAsync(5, "a", "image/png", new byte[] { 1 }, default);
            files.Trim(0);
            CollectionAssert.AreEquivalent(new[] { "5" }, Directory.GetDirectories(folder.Path).Select(Path.GetFileName).ToArray());
            Assert.IsNull(files.Find(1, "pdf/smart/00001.jpg"));
        }
    }
}
