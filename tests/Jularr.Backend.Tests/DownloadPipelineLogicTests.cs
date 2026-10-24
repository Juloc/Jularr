using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadPipelineLogicTests
    {
        [TestMethod]
        public void DownloaderSelection_PriorityWinsOverExplicitMediaScope()
        {
            var general = Client(41, 1, []);
            var scoped = Client(42, 5, [(int)MediaType.Movie]);

            Assert.AreEqual(general.Id, DownloadClientSelectionLogic.Select([scoped, general], MediaType.Movie, AcquisitionConnectionType.Sabnzbd)?.Id);
        }

        [TestMethod]
        public void DownloaderSelection_OnlyCompatibleAndEnabledClientsAreEligible()
        {
            var disabled = Client(1, 1, [], false);
            var wrongTransport = Client(2, 1, [], true, AcquisitionConnectionType.Usenet);
            var wrongMedia = Client(3, 1, [(int)MediaType.Book]);
            var correct = Client(4, 9, [(int)MediaType.Movie]);

            Assert.AreEqual(correct.Id, DownloadClientSelectionLogic.Select([disabled, wrongTransport, wrongMedia, correct],
                MediaType.Movie, AcquisitionConnectionType.Sabnzbd)?.Id);
            Assert.IsNull(DownloadClientSelectionLogic.Select([wrongMedia], MediaType.Movie, AcquisitionConnectionType.Sabnzbd));
        }

        [TestMethod]
        public void DownloaderSelection_SamePriorityUsesStableConnectionId()
        {
            var later = Client(7, 2, []);
            var earlier = Client(3, 2, []);

            Assert.AreEqual(earlier.Id, DownloadClientSelectionLogic.Select([later, earlier], MediaType.Series, AcquisitionConnectionType.Sabnzbd)?.Id);
        }

        [TestMethod]
        public void JobLayout_HasMediaAndGlobalJobIdWithoutDownloaderSegment()
        {
            Assert.AreEqual(Path.Combine("books", "j-18452"), DownloadJobLayout.RelativePath(18452, MediaType.Book, null));
            Assert.AreEqual(Path.Combine("anime", "j-18453"), DownloadJobLayout.RelativePath(18453, MediaType.Series, MediaSubtype.Anime));
            Assert.AreEqual(Path.Combine("anime", "j-18454"), DownloadJobLayout.RelativePath(18454, MediaType.Movie, MediaSubtype.Anime));
            Assert.AreEqual(Path.Combine("manga", "j-18455"), DownloadJobLayout.RelativePath(18455, MediaType.Book, MediaSubtype.Manga));
            Assert.AreEqual(Path.Combine("lightnovel", "j-18456"), DownloadJobLayout.RelativePath(18456, MediaType.Book, MediaSubtype.LightNovel));
            Assert.AreEqual(Path.Combine("tv", "j-18457"), DownloadJobLayout.RelativePath(18457, MediaType.Series, null));
            Assert.AreEqual(Path.Combine("movie", "j-18458"), DownloadJobLayout.RelativePath(18458, MediaType.Movie, null));
            Assert.AreEqual(Path.Combine("music", "j-18459"), DownloadJobLayout.RelativePath(18459, MediaType.Music, null));
            Assert.AreEqual(Path.Combine("games", "j-18460"), DownloadJobLayout.RelativePath(18460, MediaType.Game, null));
        }

        [TestMethod]
        public void JobLayout_RejectsInvalidJobIdOrMediaSubtype()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DownloadJobLayout.RelativePath(0, MediaType.Book, null));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DownloadJobLayout.RelativePath(-1, MediaType.Book, null));
            Assert.ThrowsException<ArgumentException>(() => DownloadJobLayout.RelativePath(11, MediaType.Game, MediaSubtype.Anime));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DownloadJobLayout.RelativePath(11, (MediaType)99, null));
        }

        private static AcquisitionConnectionData Client(long id, int priority, int[] mediaTypes, bool enabled = true, AcquisitionConnectionType type = AcquisitionConnectionType.Sabnzbd) =>
            new(id: id, name: "Downloader", acquisitionConnectionTypeId: type, baseUrl: "http://localhost/", protectedApiKey: null,
                isEnabled: enabled, priority: priority, categories: [], downloadCategory: null, remotePath: null, localPath: null,
                capabilities: null, version: 1, username: null, maxConnections: null, protectedPassword: null, mediaTypeIds: mediaTypes,
                searchesAutomatically: true, queryLimit: null, grabLimit: null, queryCount: 0, grabCount: 0, retryAt: null,
                host: null, port: null, useSsl: null);
    }
}
