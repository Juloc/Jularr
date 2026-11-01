using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Integrations.Data.Nyaa;
using Jularr.Integrations.Logic.Nyaa;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NyaaClientTests
    {
        [TestMethod]
        public async Task TheSearch_ReadsTheFeed_AndNamesTheFileWithTheTorrent()
        {
            var client = new FakeNyaa();
            using var http = new HttpClient(client);
            var sut = new NyaaClient(http);

            // Every category is asked; the answer of each one is read.
            var found = await sut.SearchAsync(new Uri("https://nyaa.media/"), "Orcsen", ["3_1", "3_2"], default);

            Assert.AreEqual(2, client.Requests.Count);
            StringAssert.Contains(client.Requests[0], "page=rss");
            StringAssert.Contains(client.Requests[0], "c=3_1");
            StringAssert.Contains(client.Requests[0], "q=Orcsen");
            StringAssert.Contains(client.Requests[1], "c=3_2");

            var entry = found.Single();
            Assert.AreEqual("2177106", entry.Id);
            Assert.AreEqual("History of the Kingdom of Orcsen v01-02 [Seven Seas]", entry.Title);
            Assert.AreEqual("3_1", entry.Category);
            Assert.AreEqual("Literature - English-translated", entry.CategoryName);
            Assert.AreEqual(19L * 1024 * 1024, entry.SizeBytes);
            Assert.AreEqual(23, entry.Seeders);
            Assert.AreEqual("https://nyaa.si/download/2177106.torrent", entry.DownloadUri);

            // The file with the torrent is named after the number of the entry.
            Assert.AreEqual("https://nyaa.si/download/2177106.torrent", NyaaClient.TorrentUri(entry.Id).ToString());
        }

        [TestMethod]
        public async Task ABrokenAnswer_IsReported_AndNotTakenForAnEntry()
        {
            var client = new FakeNyaa { Answer = "<rss><channel></channel></rss>" };
            using var http = new HttpClient(client);
            var sut = new NyaaClient(http);

            // An empty feed is an empty answer, not a failure.
            var found = await sut.SearchAsync(new Uri("https://nyaa.media/"), "Orcsen", ["3_1"], default);
            Assert.AreEqual(0, found.Count);

            // A feed that is no feed is a failure.
            client.Answer = "not xml at all";
            await Assert.ThrowsExactlyAsync<NyaaException>(() => sut.SearchAsync(new Uri("https://nyaa.media/"), "Orcsen", ["3_1"], default));
        }

        [TestMethod]
        public async Task TheSource_OffersTheTorrent_AndNamesTheLanguage()
        {
            var client = new FakeNyaa();
            using var http = new HttpClient(client);
            var sut = new NyaaSources(new NyaaClient(http));
            var source = new AcquisitionConnectionData(1, "Nyaa", AcquisitionConnectionType.Nyaa, "https://nyaa.media", null, true, 50, [], null, null,
                null, null, 1, null, null, null, [], true, null, null, 0, 0, null, null, null, null);
            var definition = JsonSerializer.Serialize(new AcquisitionProfileDefinition([], [], true, null, null, null)
            {
                LanguageOrder = ["de", "en"],
                LanguageFallback = LanguageFallbackType.English
            }, AcquisitionSelectionLogic.DefinitionOptions);
            var target = new WantedTargetData(1, 2, MediaType.Book, MediaSubtype.LightNovel, "History of the Kingdom of Orcsen", null,
                ["History of the Kingdom of Orcsen"], 1, "Test", definition, [], false, false, false, 0, [], ["de"], null, null, null);

            var offers = await sut.SearchAsync(source, target, default);

            // The languages of the profile decide the categories of the index.
            Assert.IsNotNull(offers);
            Assert.AreEqual(2, client.Requests.Count);
            StringAssert.Contains(client.Requests[0], "c=3_2");
            StringAssert.Contains(client.Requests[1], "c=3_1");

            // The offer names the file with the torrent and the language of the file.
            var offer = offers.Single();
            Assert.AreEqual("nyaa:2177106", offer.Guid);
            Assert.AreEqual("https://nyaa.si/download/2177106.torrent", offer.DownloadUri!.ToString());
            Assert.AreEqual("https://nyaa.media/view/2177106", offer.InfoUrl);
            Assert.AreEqual(19L * 1024 * 1024, offer.SizeBytes);
            StringAssert.Contains(offer.Title, "(EN)");
        }

        private sealed class FakeNyaa : HttpMessageHandler
        {
            public List<string> Requests { get; } = [];
            public string Answer { get; set; } = """
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0" xmlns:nyaa="https://nyaa.si/xmlns/nyaa">
                    <channel>
                        <item>
                            <title>History of the Kingdom of Orcsen v01-02 [Seven Seas]</title>
                            <guid isPermaLink="true">https://nyaa.media/view/2177106</guid>
                            <link>https://nyaa.si/download/2177106.torrent</link>
                            <pubDate>Thu, 22 Oct 2026 19:11:01 +0700</pubDate>
                            <description>19.0 MiB</description>
                            <nyaa:size>19.0 MiB</nyaa:size>
                            <nyaa:seeders>23</nyaa:seeders>
                            <nyaa:leechers>2</nyaa:leechers>
                            <nyaa:categoryId>3_1</nyaa:categoryId>
                            <nyaa:categoryName>Literature - English-translated</nyaa:categoryName>
                        </item>
                    </channel>
                </rss>
                """;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = request.RequestUri!.Query;
                Requests.Add(query);
                // The other category holds nothing for this title.
                var answer = query.Contains("c=3_2", StringComparison.Ordinal) ? "<rss><channel></channel></rss>" : Answer;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/rss+xml") });
            }
        }
    }
}
