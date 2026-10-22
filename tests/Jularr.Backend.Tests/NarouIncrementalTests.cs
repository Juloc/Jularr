using System.IO.Compression;
using System.Net;
using System.Text;
using Jularr.Integrations.Logic.Narou;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NarouIncrementalTests
    {
        [TestMethod]
        public async Task NewChaptersAreFetchedAndAppended_WithoutModifyingEarlierReaderSections()
        {
            var source = new MutableSource { ChapterCount = 2 };
            using var http = new HttpClient(source);
            var client = new NarouClient(http);
            var path = Path.Combine(Path.GetTempPath(), "jularr-narou-" + Guid.NewGuid().ToString("N") + ".epub");
            try
            {
                await client.WriteEpubAsync("n123", path, 1024 * 1024, _ => new Reservation(), CancellationToken.None);
                var originalBytes = await File.ReadAllBytesAsync(path);
                var first = Entry(originalBytes, "OEBPS/chapters/0001.xhtml");
                var second = Entry(originalBytes, "OEBPS/chapters/0002.xhtml");

                source.ChapterCount = 3;
                using var prior = new MemoryStream(originalBytes);
                var updated = await client.AppendNewChaptersAsync("n123", prior, 1024 * 1024, CancellationToken.None);
                Assert.IsNotNull(updated);
                CollectionAssert.AreEqual(first, Entry(updated, "OEBPS/chapters/0001.xhtml"));
                CollectionAssert.AreEqual(second, Entry(updated, "OEBPS/chapters/0002.xhtml"));
                StringAssert.Contains(Encoding.UTF8.GetString(Entry(updated, "OEBPS/chapters/0003.xhtml")), "第3話の本文");
                StringAssert.Contains(Encoding.UTF8.GetString(Entry(updated, "OEBPS/toc.xhtml")), "chapters/0003.xhtml");
                Assert.AreEqual(1, source.ChapterReads[3]); // no re-fetch of chapters 1 and 2

                using var current = new MemoryStream(updated);
                Assert.IsNull(await client.AppendNewChaptersAsync("n123", current, 1024 * 1024, CancellationToken.None));
                Assert.AreEqual(1, source.ChapterReads[3]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public async Task ChangedChapterOrderFailsClosed_OriginalEpubRemainsUsable()
        {
            var source = new MutableSource { ChapterCount = 2 };
            using var http = new HttpClient(source);
            var client = new NarouClient(http);
            var path = Path.Combine(Path.GetTempPath(), "jularr-narou-" + Guid.NewGuid().ToString("N") + ".epub");
            try
            {
                await client.WriteEpubAsync("n123", path, null, _ => new Reservation(), CancellationToken.None);
                var before = await File.ReadAllBytesAsync(path);
                source.ChapterCount = 3;
                source.RewriteFirstTitle = true;
                using var existing = new MemoryStream(before);
                await Assert.ThrowsExactlyAsync<NarouException>(() =>
                    client.AppendNewChaptersAsync("n123", existing, null, CancellationToken.None));
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
                Assert.IsFalse(source.ChapterReads.ContainsKey(3));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static byte[] Entry(byte[] bytes, string path)
        {
            using var stream = new MemoryStream(bytes);
            using var epub = new ZipArchive(stream, ZipArchiveMode.Read);
            using var entry = epub.GetEntry(path)!.Open();
            using var output = new MemoryStream();
            entry.CopyTo(output);
            return output.ToArray();
        }

        private sealed class Reservation : IDisposable { public void Dispose() { } }

        private sealed class MutableSource : HttpMessageHandler
        {
            public int ChapterCount { get; set; }
            public bool RewriteFirstTitle { get; set; }
            public Dictionary<int, int> ChapterReads { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                string html;
                if (segments.Length == 1)
                {
                    var links = string.Join("", Enumerable.Range(1, ChapterCount)
                        .Select(n => "<a href=\"/n123/" + n + "/\">" +
                            (n == 1 && RewriteFirstTitle ? "改題" : "第" + n + "話") + "</a>"));
                    html = "<h1 class=\"p-novel__title\">日本語の作品</h1><div class=\"p-novel__author\">著者</div>" + links;
                }
                else
                {
                    var number = int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture);
                    ChapterReads[number] = ChapterReads.GetValueOrDefault(number) + 1;
                    html = "<div id=\"novel_honbun\"><p>第" + number + "話の本文</p></div>";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }
        }
    }
}
