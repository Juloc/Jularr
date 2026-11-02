using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SubtitleTranslationTests
    {
        private const string Source = "WEBVTT\n\nNOTE comment\n\n1\n00:00:01.000 --> 00:00:02.000\nHello <i>there</i>.\n\n00:00:03.000 --> 00:00:04.500 line:10%\nSecond\nline\n\n00:00:05.000 --> 00:00:06.000\n<c.sound> </c>\n\n00:00:07.000 --> 00:00:08.000\nBye.\n";

        [TestMethod]
        public void Cues_AreParsedBatchedAndComposedWithTheirOwnTimes()
        {
            var cues = SubtitleTranslationLogic.ParseCues(Source);
            CollectionAssert.AreEqual(new[] { "00:00:01.000 --> 00:00:02.000", "00:00:03.000 --> 00:00:04.500 line:10%", "00:00:05.000 --> 00:00:06.000", "00:00:07.000 --> 00:00:08.000" },
                cues.Select(cue => cue.Timing).ToArray());
            Assert.AreEqual("Hello there .", SubtitleTranslationLogic.PlainText(cues[0].Text).Replace(" .", " ."));
            Assert.AreEqual("Second line", SubtitleTranslationLogic.PlainText(cues[1].Text));

            // The sound mark has no words and stays out of the batches.
            var batches = SubtitleTranslationLogic.Batches(cues);
            Assert.AreEqual(1, batches.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 3 }, batches[0].ToArray());
            Assert.AreEqual("[1] Hello there .\n[2] Second line\n[3] Bye.", SubtitleTranslationLogic.BatchText(cues, batches[0]));

            var composed = SubtitleTranslationLogic.Compose(cues, ["Hallo.", "Zweite Zeile", null, "Tschüss."]);
            StringAssert.StartsWith(composed, "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHallo.\n\n00:00:03.000 --> 00:00:04.500 line:10%\nZweite Zeile\n\n");
            StringAssert.Contains(composed, "<c.sound> </c>");
        }

        [TestMethod]
        public void Batches_AreSplitByCountAndAnswersByNumber()
        {
            var many = SubtitleTranslationLogic.ParseCues(string.Concat(Enumerable.Range(0, 30).Select(index => $"\n\n00:00:{index:00}.000 --> 00:00:{index:00}.500\nLine {index}")));
            Assert.AreEqual(3, SubtitleTranslationLogic.Batches(many).Count);

            CollectionAssert.AreEqual(new[] { "Eins", "Zwei" }, SubtitleTranslationLogic.ParseBatch("[2] Zwei\n[1] Eins\nnoise", 2));
            Assert.IsNull(SubtitleTranslationLogic.ParseBatch("[1] Eins", 2));
            Assert.IsNull(SubtitleTranslationLogic.ParseBatch("[1] Eins\n[1] Doppelt", 2));

            Assert.AreEqual("x-de-s5", SubtitleTranslationLogic.Key("de", "s5"));
            Assert.IsTrue(SubtitleTranslationLogic.TryParseKey("x-de-f12", out var language, out var source));
            Assert.AreEqual(("de", "f12"), (language, source));
            Assert.IsFalse(SubtitleTranslationLogic.TryParseKey("s5", out _, out _));
            Assert.IsFalse(SubtitleTranslationLogic.TryParseKey("x-d-s5", out _, out _));
        }

        [TestMethod]
        public async Task Translation_RunsInTheBackgroundWithTheLocalModelAndServesItsSubtitle()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string>
                {
                    ["Media:FfmpegPath"] = Path.Combine(cache.Path, "missing-ffmpeg"),
                    ["Media:TranscodeCachePath"] = cache.Path,
                    ["LocalTranslation:Endpoint"] = "http://ollama:11434"
                },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Movies", "Film (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.mp4"), "jklmnopq");
            File.WriteAllText(Path.Combine(folder, "Film.en.srt"), "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nBye.\r\n");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.mp4'"))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Film", null, null, null));
            var assetId = (await assigned.Content.ReadFromJsonAsync<AssignLibraryFileResultV1>())!.MediaAssetId;
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/tracks");
            var sidecar = (await listed.Content.ReadFromJsonAsync<BaseDataList<PlaybackTrackV1>>())!.Single(track => track.Language == "en" && track.Key.StartsWith('f'));
            var route = "/api/v1/playback/assets/" + assetId + "/subtitle-translations";

            // The module is the administrator's switch: off, nothing is translated.
            using var off = await host.SendAsync(HttpMethod.Post, route, new StartSubtitleTranslationDataV1(sidecar.Key, "en", "de"));
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            using var same = await host.SendAsync(HttpMethod.Post, route, new StartSubtitleTranslationDataV1(sidecar.Key, "en", "en"));
            Assert.AreEqual(HttpStatusCode.BadRequest, same.StatusCode);

            using var started = await host.SendAsync(HttpMethod.Post, route, new StartSubtitleTranslationDataV1(sidecar.Key, "en", "de"));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var translation = (await started.Content.ReadFromJsonAsync<SubtitleTranslationV1>())!;
            Assert.AreEqual("x-de-" + sidecar.Key, translation.Key);

            for (var attempt = 0; attempt < 100 && translation.State != "done"; attempt++)
            {
                await Task.Delay(100);
                using var status = await host.SendAsync(HttpMethod.Get, route + "/" + translation.Key);
                Assert.AreEqual(HttpStatusCode.OK, status.StatusCode, await status.Content.ReadAsStringAsync());
                translation = (await status.Content.ReadFromJsonAsync<SubtitleTranslationV1>())!;
            }

            Assert.AreEqual("done", translation.State);
            using var subtitle = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/subtitles/" + translation.Key);
            Assert.AreEqual(HttpStatusCode.OK, subtitle.StatusCode, await subtitle.Content.ReadAsStringAsync());
            Assert.AreEqual("text/vtt", subtitle.Content.Headers.ContentType!.MediaType);
            var vtt = await subtitle.Content.ReadAsStringAsync();
            StringAssert.Contains(vtt, "00:00:01.000 --> 00:00:02.000\nHallo.");
            StringAssert.Contains(vtt, "00:00:03.000 --> 00:00:04.000\nTschüss.");
            Assert.AreEqual(1, ollama.GenerationCount, "Both cues are one batch.");

            // The same request again is the finished translation, not a new one; a subtitle that was never asked for does not exist.
            using var again = await host.SendAsync(HttpMethod.Post, route, new StartSubtitleTranslationDataV1(sidecar.Key, "en", "de"));
            Assert.AreEqual("done", (await again.Content.ReadFromJsonAsync<SubtitleTranslationV1>())!.State);
            Assert.AreEqual(1, ollama.GenerationCount);
            using var other = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/subtitles/x-fr-" + sidecar.Key);
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public int GenerationCount { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                {
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = new string('a', 64) } } });
                }
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var prompt = input.RootElement.GetProperty("prompt").GetString()!;
                    StringAssert.Contains(prompt, "[1] Hello.\n[2] Bye.");
                    GenerationCount++;
                    body = """{"response":"[1] Hallo.\n[2] Tschüss.","eval_count":10,"eval_duration":1000000000}""";
                }
                else
                {
                    throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
