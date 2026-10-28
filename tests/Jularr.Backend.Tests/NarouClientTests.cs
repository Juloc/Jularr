using System.IO.Compression;
using System.Net;
using System.Text;
using Jularr.Backend.Logic.Reading;
using Jularr.Integrations.Data.Narou;
using Jularr.Integrations.Logic.Narou;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NarouClientTests
    {
        [TestMethod]
        public async Task Search_ByTitleOrNcode_AsksOnlyTheOfficialApi()
        {
            var narou = new FakeNarou();
            using var http = new HttpClient(narou);
            var sut = new NarouClient(http);

            // A title searches titles by rating; an ncode names its one novel.
            var found = (await sut.SearchAsync("転生したら", default)).Single();
            Assert.AreEqual(("n1234ab", "転生したら図書館だった件", "山田太郎", 2019, false, 3), (found.Ncode, found.Title, found.Writer, found.FirstYear, found.IsShortStory, found.ChapterCount));
            Assert.AreEqual("司書が異世界で本を集める。", found.Story);
            StringAssert.Contains(narou.Requests[0], "title=1");
            StringAssert.Contains(narou.Requests[0], "word=" + Uri.EscapeDataString("転生したら"));
            await sut.SearchAsync("N1234AB", default);
            StringAssert.Contains(narou.Requests[1], "ncode=n1234ab");
            Assert.IsTrue(narou.Requests.All(request => request.StartsWith(NarouClient.Api, StringComparison.Ordinal)));

            // An answer that is no list is a failure, not an empty result.
            narou.Api = "{}";
            Assert.AreEqual(NarouFailure.InvalidResponse, (await Assert.ThrowsExactlyAsync<NarouException>(() => sut.SearchAsync("転生", default))).Failure);
        }

        [TestMethod]
        public void Chapter_KeepsItsTextAndRuby_WithoutForewordAndAfterword()
        {
            var chapter = NarouClient.ParseChapter(FakeNarou.ChapterPage(2, "第二話　出会い", "<ruby>司書<rp>(</rp><rt>ししょ</rt><rp>)</rp></ruby>は<b>本</b>を&amp;読む。"), 2, null);

            Assert.AreEqual("第二話　出会い", chapter.Title);
            Assert.AreEqual("<p><ruby>司書<rt>ししょ</rt></ruby>は本を&amp;読む。</p><p><br/></p><p>二行目 2</p>", chapter.Body);
            Assert.IsFalse(chapter.Body.Contains("前書き", StringComparison.Ordinal));
            Assert.IsFalse(chapter.Body.Contains("後書き", StringComparison.Ordinal));
            Assert.AreEqual(NarouFailure.InvalidResponse, Assert.ThrowsExactly<NarouException>(() => NarouClient.ParseChapter("<html><body>maintenance</body></html>", 1, null)).Failure);
        }

        [TestMethod]
        public async Task Download_KeepsChaptersThroughAFailure_ResumesAndWritesAReadableEpub()
        {
            using var folder = new TemporaryDirectory();
            var destination = Path.Combine(folder.Path, "book.epub.part");
            var narou = new FakeNarou { FailingChapter = 3 };
            using var http = new HttpClient(narou);
            var sut = new NarouClient(http);
            IDisposable Reserve(long bytes) => new MemoryStream();

            // Narō fails at the third chapter: the first two stay and the job comes back later.
            var first = await sut.DownloadAsync("n1234ab", destination, null, Reserve, default);
            Assert.AreEqual((2, 3, false), (first.Done, first.Total, first.IsComplete));
            Assert.IsNotNull(first.RetryAfter);
            Assert.IsFalse(File.Exists(destination));

            // The next attempt asks only for the missing chapter, then writes the book and leaves no kept chapters behind.
            narou.FailingChapter = null;
            narou.Requests.Clear();
            var second = await sut.DownloadAsync("n1234ab", destination, null, Reserve, default);
            Assert.AreEqual((3, 3, true), (second.Done, second.Total, second.IsComplete));
            CollectionAssert.AreEqual(new[] { NarouClient.Site + "n1234ab/3/" }, narou.Requests);
            Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "narou")));

            // The reader takes the book: Japanese, the novel's title, one section per chapter in order.
            using var archive = ZipFile.OpenRead(destination);
            var package = EpubReader.Read(archive);
            Assert.AreEqual(("転生したら図書館だった件", "ja", 3), (package.Title, package.Language, package.Spine.Count));
            CollectionAssert.AreEqual(new[] { "第1話", "第2話", "第3話" }, package.Toc.Select(item => item.Title).ToArray());
            using var reader = new StreamReader(archive.GetEntry("OEBPS/content.opf")!.Open(), Encoding.UTF8);
            StringAssert.Contains(await reader.ReadToEndAsync(), "urn:syosetu:n1234ab");
        }

        [TestMethod]
        public async Task Download_GivesUpAfterRepeatedFailuresWithoutProgress()
        {
            using var folder = new TemporaryDirectory();
            var destination = Path.Combine(folder.Path, "book.epub.part");
            var narou = new FakeNarou { FailingChapter = 1 };
            using var http = new HttpClient(narou);
            var sut = new NarouClient(http);
            IDisposable Reserve(long bytes) => new MemoryStream();

            for (var attempt = 1; attempt < 5; attempt++)
            {
                Assert.IsNotNull((await sut.DownloadAsync("n1234ab", destination, null, Reserve, default)).RetryAfter);
            }

            Assert.AreEqual(NarouFailure.Unavailable, (await Assert.ThrowsExactlyAsync<NarouException>(() => sut.DownloadAsync("n1234ab", destination, null, Reserve, default))).Failure);
        }

        // Narō's API and novel pages as they answer today; only its two hosts exist.
        internal sealed class FakeNarou : HttpMessageHandler
        {
            public List<string> Requests { get; } = [];
            public int? FailingChapter { get; set; }
            public string? Api { get; set; }

            // When set, a title search finds the novel only under this word.
            public string? Word { get; set; }

            public static string Novel => """
                [{"allcount":1},{"title":"転生したら図書館だった件","ncode":"N1234AB","writer":"山田太郎","story":"司書が異世界で本を集める。","general_firstup":"2019-04-01 12:00:00","novel_type":1,"end":1,"general_all_no":3}]
                """;

            public static string ChapterPage(int number, string title, string firstLine) => $"""
                <html><body><div class="p-novel">
                <h1 class="p-novel__title p-novel__title--rensai">{title}</h1>
                <div class="p-novel__body">
                <div class="js-novel-text p-novel__text p-novel__text--preface"><p id="Lp1">前書き</p></div>
                <div class="js-novel-text p-novel__text"><p id="L1">{firstLine}</p><p id="L2"><br /></p><p id="L3"><br /></p><p id="L4">二行目 {number}</p></div>
                <div class="js-novel-text p-novel__text p-novel__text--afterword"><p id="La1">後書き</p></div>
                </div></div></body></html>
                """;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var address = request.RequestUri!.AbsoluteUri;
                Requests.Add(address);
                if (address.StartsWith(NarouClient.Api, StringComparison.Ordinal) && Word is not null && address.Contains("word=", StringComparison.Ordinal)
                    && !address.EndsWith("word=" + Uri.EscapeDataString(Word), StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"allcount\":0}]", Encoding.UTF8, "application/json") });
                }

                if (address.StartsWith(NarouClient.Api, StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Api ?? Novel, Encoding.UTF8, "application/json") });
                }

                var path = request.RequestUri.AbsolutePath.Trim('/').Split('/');
                if (request.RequestUri.Host == "ncode.syosetu.com" && path is ["n1234ab", var text] && int.TryParse(text, out var number) && number is >= 1 and <= 3)
                {
                    return Task.FromResult(number == FailingChapter ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ChapterPage(number, "第" + number + "話", "本文 " + number), Encoding.UTF8, "text/html") });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
