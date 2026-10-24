using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadClientSelectionTests
    {
        private static readonly DateTime Clock = new(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void DownloaderSelection_PriorityBeforeMediaScope()
        {
            var general = Client(41, 1, []);
            var scoped = Client(42, 5, [(int)MediaType.Movie]);
            Assert.AreEqual(general.Id, DownloadClientSelectionLogic.Select([scoped, general], MediaType.Movie, Clock, AcquisitionConnectionType.Sabnzbd)?.Id);
        }

        [TestMethod]
        public void DownloaderSelection_RequiresCompatibleTypeEnabledAndSupportedMedia()
        {
            var disabled = Client(1, 1, [], false);
            var wrongType = Client(2, 1, [], true, AcquisitionConnectionType.Usenet);
            var wrongMedia = Client(3, 1, [(int)MediaType.Book]);
            var correct = Client(4, 9, [(int)MediaType.Movie]);
            Assert.AreEqual(correct.Id, DownloadClientSelectionLogic.Select([disabled, wrongType, wrongMedia, correct],
                MediaType.Movie, Clock, AcquisitionConnectionType.Sabnzbd)?.Id);
            Assert.IsNull(DownloadClientSelectionLogic.Select([wrongMedia], MediaType.Movie, Clock, AcquisitionConnectionType.Sabnzbd));
        }

        [TestMethod]
        public void DownloaderSelection_TiesResolveByStableConnectionId()
        {
            var later = Client(7, 2, []);
            var earlier = Client(3, 2, []);
            Assert.AreEqual(earlier.Id, DownloadClientSelectionLogic.Select([later, earlier], MediaType.Series, Clock, AcquisitionConnectionType.Sabnzbd)?.Id);
        }

        [TestMethod]
        public void DownloaderSelection_BackoffExcludesUnavailableHighPriorityClient()
        {
            var recovering = Client(1, 1, [], retryAt: Clock.AddMinutes(10));
            var ready = Client(2, 3, []);
            Assert.AreEqual(ready.Id, DownloadClientSelectionLogic.Select([recovering, ready],
                MediaType.Movie, Clock, AcquisitionConnectionType.Sabnzbd)?.Id);
            Assert.AreEqual(recovering.Id, DownloadClientSelectionLogic.Select([recovering, ready],
                MediaType.Movie, Clock.AddMinutes(11), AcquisitionConnectionType.Sabnzbd)?.Id);
        }

        [TestMethod]
        public void JobLayout_UsesOnlyMediaCategoryAndSharedOperationId()
        {
            Assert.AreEqual(Path.Combine("movie", "j-41"), DownloadWorkspaceLayout.Relative(41, MediaType.Movie, null));
            Assert.AreEqual(Path.Combine("books", "j-42"), DownloadWorkspaceLayout.Relative(42, MediaType.Book, null));
            Assert.AreEqual(Path.Combine("anime", "j-43"), DownloadWorkspaceLayout.Relative(43, MediaType.Series, MediaSubtype.Anime));
            Assert.AreEqual(Path.Combine("lightnovel", "j-44"), DownloadWorkspaceLayout.Relative(44, MediaType.Book, MediaSubtype.LightNovel));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DownloadWorkspaceLayout.JobName(0));
            Assert.ThrowsExactly<ArgumentException>(() => DownloadWorkspaceLayout.Relative(44, MediaType.Game, MediaSubtype.Anime));
        }

        [TestMethod]
        public void TorrentJobFolders_UseDownloaderRootAndSameMediaJobIdentity()
        {
            Assert.AreEqual("/downloads/complete/books/j-18452",
                DownloadWorkspaceLayout.ExternalJobDirectory("/downloads/complete/", 18452, MediaType.Book, null));
            Assert.AreEqual("/downloads/complete/anime/j-18453",
                DownloadWorkspaceLayout.ExternalJobDirectory("/downloads/complete", 18453, MediaType.Series, MediaSubtype.Anime));
            Assert.AreEqual(@"D:\Downloads\manga\j-18454",
                DownloadWorkspaceLayout.ExternalJobDirectory(@"D:\Downloads\", 18454, MediaType.Book, MediaSubtype.Manga));
            Assert.ThrowsExactly<ArgumentException>(() =>
                DownloadWorkspaceLayout.ExternalJobDirectory("/", 18455, MediaType.Movie, null));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                DownloadWorkspaceLayout.ExternalJobDirectory("/downloads/complete", 0, MediaType.Movie, null));
        }

        private static AcquisitionConnectionData Client(long id, int priority, int[] mediaTypes, bool enabled = true,
            AcquisitionConnectionType type = AcquisitionConnectionType.Sabnzbd, DateTime? retryAt = null) =>
            new(id: id, name: "Downloader", acquisitionConnectionTypeId: type, baseUrl: "http://localhost/", protectedApiKey: null,
                isEnabled: enabled, priority: priority, categories: [], downloadCategory: null, remotePath: null, localPath: null,
                capabilities: null, version: 1, username: null, maxConnections: null, protectedPassword: null, mediaTypeIds: mediaTypes,
                searchesAutomatically: true, queryLimit: null, grabLimit: null, queryCount: 0, grabCount: 0, retryAt: retryAt,
                host: null, port: null, useSsl: null);
    }
}
