using Jularr.Backend.Service.Admin.Wanted.V1;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TorrentResumeTests
    {
        private const string Hash = "d64a4efe53a5170b28ecf89232d421d349229910";
        private const string OtherHash = "0123456789abcdef0123456789abcdef01234567";

        private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

        private static string Job(string category, long id, string hash)
        {
            var directory = Directory.CreateDirectory(Path.Combine(category, "j-" + id)).FullName;
            File.WriteAllText(Path.Combine(directory, "job.magnet"), "magnet:?xt=urn:btih:" + hash);
            return directory;
        }

        [TestMethod]
        public async Task TakeOver_AnEarlierTryOfTheSameTorrent_KeepsItsPiecesAndMetadata()
        {
            using var root = new TemporaryDirectory();
            var earlier = Job(root.Path, 1, Hash);
            Directory.CreateDirectory(Path.Combine(earlier, "data"));
            File.WriteAllText(Path.Combine(earlier, "data", "volume.epub"), "pieces");
            File.WriteAllText(Path.Combine(earlier, "job.state"), "state");
            File.WriteAllText(Path.Combine(earlier, "job.info"), "info");
            var staging = Job(root.Path, 2, Hash);

            var taken = await TorrentResume.TakeOverAsync(staging, Bytes(Hash), _ => Task.FromResult(false), CancellationToken.None);

            Assert.IsTrue(taken);
            Assert.AreEqual("pieces", File.ReadAllText(Path.Combine(staging, "data", "volume.epub")));
            Assert.AreEqual("state", File.ReadAllText(Path.Combine(staging, "job.state")));
            Assert.AreEqual("info", File.ReadAllText(Path.Combine(staging, "job.info")));
            Assert.IsTrue(File.Exists(Path.Combine(staging, "job.magnet")), "The new try keeps its own magnet file.");
            Assert.IsFalse(Directory.Exists(earlier), "The earlier folder is gone once its pieces moved.");
        }

        [TestMethod]
        public async Task TakeOver_AnotherTorrent_OrAFolderWhoseJobStillRuns_IsLeftAlone()
        {
            using var root = new TemporaryDirectory();
            var other = Job(root.Path, 1, OtherHash);
            File.WriteAllText(Path.Combine(other, "job.state"), "other");
            var running = Job(root.Path, 2, Hash);
            File.WriteAllText(Path.Combine(running, "job.state"), "running");
            var staging = Job(root.Path, 3, Hash);

            var taken = await TorrentResume.TakeOverAsync(staging, Bytes(Hash), id => Task.FromResult(id == 2), CancellationToken.None);

            Assert.IsFalse(taken);
            Assert.IsTrue(File.Exists(Path.Combine(other, "job.state")));
            Assert.IsTrue(File.Exists(Path.Combine(running, "job.state")));
            Assert.IsFalse(File.Exists(Path.Combine(staging, "job.state")));
        }
    }
}
