using System.IO.Compression;
using System.Net;
using System.Text;
using Jularr.Integrations.Logic.Narou;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NarouClientTests
    {
        [TestMethod]
        public async Task Narou_ImportsPublicChaptersAsValidEpub()
        {
            using var http = new HttpClient(new NarouHandler());
            var client = new NarouClient(http);
            var work = await client.ReadWorkAsync("N123", CancellationToken.None);
            Assert.AreEqual("テスト作品", work.Title);
            Assert.AreEqual("著者", work.Author);
            Assert.AreEqual(2, work.Chapters.Count);

            var path = Path.Combine(Path.GetTempPath(), "jularr-narou-" + Guid.NewGuid().ToString("N") + ".epub");
            try
            {
                await client.WriteEpubAsync("N123", path, 1024 * 1024, _ => new Reservation(), CancellationToken.None);
                using var epub = ZipFile.OpenRead(path);
                Assert.AreEqual("application/epub+zip", Read(epub, "mimetype"));
                StringAssert.Contains(Read(epub, "OEBPS/content.opf"), "urn:syosetu:n123");
                StringAssert.Contains(Read(epub, "OEBPS/content.opf"), "xml");
                StringAssert.Contains(Read(epub, "OEBPS/toc.xhtml"), "第2話");
                StringAssert.Contains(Read(epub, "OEBPS/chapters/0001.xhtml"), "第一段落");
                StringAssert.Contains(Read(epub, "OEBPS/chapters/0002.xhtml"), "続き");
                Assert.IsFalse(Read(epub, "OEBPS/chapters/0002.xhtml").Contains("無関係な前書き", StringComparison.Ordinal));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public async Task Narou_RejectsInvalidNcodeAndIncompleteChapter()
        {
            Assert.IsFalse(NarouClient.IsNcode("https://example.com"));
            Assert.IsFalse(NarouClient.IsNcode("../n123"));
            using var http = new HttpClient(new NarouHandler(noBody: true));
            var client = new NarouClient(http);
            await Assert.ThrowsExactlyAsync<NarouException>(() =>
                client.WriteEpubAsync("n123", Path.GetTempFileName(), 1024 * 1024, _ => new Reservation(), CancellationToken.None));
        }

        private static string Read(ZipArchive archive, string name)
        {
            using var input = archive.GetEntry(name)!.Open();
            using var reader = new StreamReader(input, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private sealed class Reservation : IDisposable { public void Dispose() { } }

        private sealed class NarouHandler(bool noBody = false) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!;
                var html = url.AbsolutePath.EndsWith("/1/", StringComparison.Ordinal)
                    ? noBody ? "<h1>No text</h1>" : "<div id=\"novel_honbun\"><p>第一段落</p><p>第二段落</p></div>"
                    : url.AbsolutePath.EndsWith("/2/", StringComparison.Ordinal)
                    ? "<div class=\"js-novel-text p-novel__text p-novel__text--preface\">無関係な前書き</div><div class=\"js-novel-text p-novel__text\">続き</div>"
                    : url.Query.Contains("p=2", StringComparison.Ordinal)
                    ? "<a href=\"/n123/2/\">第2話</a>"
                    : "<h1 class=\"p-novel__title\">テスト作品</h1><div class=\"p-novel__author\">著者</div>" +
                      "<a href=\"/n123/1/\">第1話</a><a href=\"?p=2\">Next</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }
        }
    }
}
