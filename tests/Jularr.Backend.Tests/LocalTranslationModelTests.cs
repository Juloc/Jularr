using System.Net;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using Jularr.Integrations.Logic.Translation;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LocalTranslationModelTests
    {
        private static readonly Uri Ollama = new("http://ollama:11434/");

        [TestMethod]
        public async Task Machines_TranslateInTheOwnersOrder_PassOverOneThatFails_AndReturnToItAfterItsRest()
        {
            var handler = new Machines();
            using var http = new HttpClient(handler);
            var clock = new Clock();
            var machines = new LocalTranslationMachines(new OllamaLocalModels(http),
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434" }), clock);

            // Nothing saved: the configured endpoint alone, so an upgraded install translates as before; addresses outside the private network never count.
            CollectionAssert.AreEqual(new[] { Ollama }, machines.Order([]));
            CollectionAssert.AreEqual(new[] { new Uri("http://192.168.1.20:11434/"), new Uri("http://10.0.0.2:11434/") },
                machines.Order(["http://192.168.1.20:11434", "http://8.8.8.8:11434", "http://10.0.0.2:11434", "http://192.168.1.20:11434"]));

            string[] saved = ["http://192.168.1.20:11434", "http://10.0.0.2:11434"];
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), (await machines.ChooseAsync(saved, default))?.Server);

            // The first drops out: the next takes over, and the first rests even once it answers again.
            handler.Offline = "192.168.1.20";
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), (await machines.ChooseAsync(saved, default))?.Server);
            handler.Offline = null;
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), (await machines.ChooseAsync(saved, default))?.Server);
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), (await machines.ChooseAsync(saved, default))?.Server);

            // A machine that failed a block rests too; a machine without the wanted model is passed over.
            machines.Fail(new Uri("http://192.168.1.20:11434/"));
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), (await machines.ChooseAsync(saved, default))?.Server);
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), (await machines.ChooseAsync(saved, default, "translategemma:12b"))?.Server);
            Assert.IsNull(await machines.ChooseAsync(saved, default, "translategemma:27b"));
            // Within one machine prefer 12B over 4B, without changing the owner's machine priority.
            Assert.AreEqual("translategemma:12b", (await machines.ChooseAsync(["http://10.0.0.2:11434"], default))?.Model);
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        // Two machines: 192.168.1.20 with the small model, 10.0.0.2 with the small and the middle one.
        private sealed class Machines : HttpMessageHandler
        {
            public string? Offline { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.Host == Offline) throw new HttpRequestException("No route to host.");
                var models = request.RequestUri.Host == "10.0.0.2" ? """[{"name":"translategemma:4b"},{"name":"translategemma:12b"}]""" : """[{"name":"translategemma:4b"}]""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":" + models + "}") });
            }
        }
        [TestMethod]
        public void Endpoint_AllowsOnlyLocalOrPrivateTargets()
        {
            foreach (var value in new[] { "http://ollama:11434", "http://127.0.0.1:11434", "http://10.0.0.4:11434", "https://192.168.1.30" })
                Assert.IsNotNull(OllamaLocalModels.Endpoint(value), value);

            foreach (var value in new[] { "https://example.org", "http://169.254.169.254", "http://8.8.8.8",
                         "http://user:pass@ollama:11434", "http://ollama:11434/other", "file:///etc/passwd" })
                Assert.IsNull(OllamaLocalModels.Endpoint(value), value);
        }

        [TestMethod]
        public async Task Client_ListsApprovedModels_AndBenchmarksWithBoundedInput()
        {
            var handler = new FakeOllama();
            using var http = new HttpClient(handler);
            var client = new OllamaLocalModels(http);
            var server = new Uri("http://ollama:11434/");
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.InstalledAsync(server, default));
            Assert.AreEqual(new string('a', 64), await client.DigestAsync(server, "translategemma:4b", default));
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.RunningAsync(server, default));
            var result = await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default);
            Assert.AreEqual("Rina ging nach Hause.", result.Text);
            Assert.AreEqual(10.0, result.TokensPerSecond);
            await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default, "Rina tinggal di kota.");
            StringAssert.Contains(handler.LastPrompt!, "Earlier source passage for context only");
            StringAssert.Contains(handler.LastPrompt!, "Rina tinggal di kota.");
            Assert.IsTrue(handler.LastPrompt!.EndsWith("into German:\n\n\nRina pulang.", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "untrusted:90b", "id", "de", "Rina pulang.", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "translategemma:4b", "id", "de", new string('x', 20001), default));
        }

        [TestMethod]
        public async Task Block_TheModelTranslatesTheContextToo_IsAskedAgainWithoutIt()
        {
            var handler = new EchoingOllama();
            using var http = new HttpClient(handler);
            var client = new OllamaLocalModels(http);

            // With context the answer holds the context's translation as well: more paragraphs than the block.
            Assert.AreEqual("Rina ging nach Hause.", await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang.", "Rina tinggal di kota.", default));
            Assert.AreEqual(2, handler.Calls);
            Assert.IsFalse(handler.LastPrompt!.Contains("Earlier source passage", StringComparison.Ordinal));

            // Without context the first answer stands.
            handler.Calls = 0;
            Assert.AreEqual("Rina ging nach Hause.", await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang.", null, default));
            Assert.AreEqual(1, handler.Calls);

            // An answer that still fails the checks is none.
            handler.Bad = true;
            Assert.IsNull(await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang. Dia makan.", "Rina tinggal di kota.", default));
        }

        private sealed class EchoingOllama : HttpMessageHandler
        {
            public int Calls { get; set; }
            public bool Bad { get; set; }
            public string? LastPrompt { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                LastPrompt = json.RootElement.GetProperty("prompt").GetString()!;
                var answer = Bad ? "Rina pulang. Dia makan." : LastPrompt.Contains("Earlier source passage", StringComparison.Ordinal)
                    ? "Rina wohnt in der Stadt.\n\nRina ging nach Hause." : "Rina ging nach Hause.";
                var body = JsonSerializer.Serialize(new { response = answer, eval_count = 20, eval_duration = 2000000000 });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }

        [TestMethod]
        public void QualityGate_RejectsEchoedOrMostlyUntranslatedJapanese()
        {
            var source = "私は三十四歳で、仕事がなく、家もありません。";
            Assert.IsTrue(OllamaLocalModels.LooksComplete(source, "Ich bin 34 Jahre alt, arbeitslos und obdachlos.", "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, source, "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, "私は三十四歳で仕事も家もありません。", "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, "x", "de"));
        }

        [TestMethod]
        public void ProviderPaths_KeepModelsAndTargetsSeparateFromBergamot()
        {
            var first = LocalReadingPaths.Cache(12, "de", "translategemma:4b");
            var second = LocalReadingPaths.Cache(12, "de", "translategemma:12b");
            Assert.AreNotEqual(first, second);
            Assert.AreNotEqual(first, LocalReadingPaths.Cache(12, "id", "translategemma:4b"));
            Assert.AreNotEqual(first, Jularr.Backend.Logic.Reading.ReadingTranslations.Path(12, "de"));
            Assert.IsFalse(first.StartsWith(":translation/", StringComparison.Ordinal), "Local drafts cannot be counted as finished languages.");
        }

        [TestMethod]
        public void Cache_RejectsStaleSourceLanguageDigestAndAlgorithm()
        {
            const string html = "<p data-b=\"0\">Rina pulang.</p>";
            var digest = new string('a', 64);
            var source = LocalReadingPaths.SourceHash(html, "id");
            var cache = new LocalTranslationCache(LocalReadingPaths.Algorithm, "id", source, digest, ["Rina ging nach Hause.", null!]);
            var json = JsonSerializer.Serialize(cache);
            Assert.IsNotNull(LocalReadingPaths.Parse(json, html, "id", digest, 2), "A partial cache must survive refresh.");
            Assert.IsNull(LocalReadingPaths.Parse(json, html, "ja", digest, 2));
            Assert.IsNull(LocalReadingPaths.Parse(json, html + "changed", "id", digest, 2));
            Assert.IsNull(LocalReadingPaths.Parse(json, html, "id", new string('b', 64), 2));
            Assert.IsNull(LocalReadingPaths.Parse(json, html, "id", digest, 1));
            Assert.IsNull(LocalReadingPaths.Parse("{broken", html, "id", digest, 2));
        }

        [TestMethod]
        public void Chunker_LongParagraph_KeepsOrderedTextAndBoundedUnits()
        {
            var text = string.Join(' ', Enumerable.Repeat("Rina ging nach Hause.", 300));
            var chunks = OllamaLocalModels.Segments(text);
            Assert.IsTrue(chunks.Count > 1);
            Assert.IsTrue(chunks.All(chunk => chunk.Length is > 0 and <= 2400));
            Assert.AreEqual(text, string.Join(' ', chunks));
            Assert.ThrowsExactly<ArgumentException>(() => OllamaLocalModels.Segments(new string('a', 20001)));
        }

        [TestMethod]
        public void DownloadQueue_DeniesModelsWithoutOperatorVerifiedVolumeBudget()
        {
            using var http = new HttpClient(new FakeOllama());
            var client = new OllamaLocalModels(http);
            var root = Path.Combine(Path.GetTempPath(), "jularr-local-pull-" + Guid.NewGuid().ToString("N"));
            using var noCapacity = new LocalTranslationDownloads(client,
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 0 }),
                Options.Create(new MediaOptions { TranslationPath = Path.Combine(root, "denied") }),
                new LocalTranslationGate());
            Assert.IsFalse(noCapacity.Queue("translategemma:4b", Ollama, 0));
            Assert.IsFalse(noCapacity.Queue("other:27b", Ollama, 0));

            using var adequate = new LocalTranslationDownloads(client,
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L }),
                Options.Create(new MediaOptions { TranslationPath = Path.Combine(root, "approved") }),
                new LocalTranslationGate());
            Assert.IsTrue(adequate.Queue("translategemma:4b", Ollama, 6_000_000_000L));
            Assert.IsFalse(adequate.Queue("translategemma:12b", Ollama, 6_000_000_000L));
            Assert.AreEqual("queued", adequate.Current().State);
        }

        [TestMethod]
        public void DownloadStatus_SurvivesRestart_AsInterruptedUntilExplicitRetry()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-pull-restart-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var http = new HttpClient(new FakeOllama());
                var client = new OllamaLocalModels(http);
                var settings = Options.Create(new LocalTranslationOptions
                {
                    Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L
                });
                var media = Options.Create(new MediaOptions { TranslationPath = root });
                using (var beforeRestart = new LocalTranslationDownloads(client, settings, media, new LocalTranslationGate()))
                {
                    Assert.IsTrue(beforeRestart.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                    Assert.AreEqual("queued", beforeRestart.Current().State);
                    Assert.IsTrue(File.Exists(Path.Combine(root, "ollama-pull-state.json")));
                }
                // No automatic network activity: restart reports interruption and the owner
                // explicitly retries the same model, whose partial blobs Ollama retains.
                using (var afterRestart = new LocalTranslationDownloads(client, settings, media, new LocalTranslationGate()))
                {
                    Assert.AreEqual("interrupted", afterRestart.Current().State);
                    Assert.AreEqual("translategemma:4b", afterRestart.Current().Model);
                    Assert.IsTrue(afterRestart.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                    Assert.AreEqual("queued", afterRestart.Current().State);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void DownloadStatus_RejectsCorruptPersistentJob_FailsClosed()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-pull-corrupt-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "ollama-pull-state.json"), "{invalid-json");
                using var http = new HttpClient(new FakeOllama());
                using var downloads = new LocalTranslationDownloads(new OllamaLocalModels(http),
                    Options.Create(new LocalTranslationOptions
                    {
                        Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L
                    }),
                    Options.Create(new MediaOptions { TranslationPath = root }),
                    new LocalTranslationGate());
                Assert.AreEqual("failed", downloads.Current().State);
                Assert.IsFalse(downloads.Queue("translategemma:4b", Ollama, 6_000_000_000L));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task FreeSpace_TypedByTheOwner_IsReadBackAndANegativeValueIsRefused()
        {
            await using var host = await LibraryTests.CreateHostAsync(Path.Combine(Path.GetTempPath(), "jularr-free-" + Guid.NewGuid().ToString("N")), razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            using var save = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/free-bytes", new TranslationFreeBytesDataV1(50_000_000_000L));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/models");
            using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.AreEqual(50_000_000_000L, json.RootElement.GetProperty("verifiedFreeBytes").GetInt64());

            using var negative = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/free-bytes", new TranslationFreeBytesDataV1(-1));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, negative.StatusCode);
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public string? LastPrompt { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/tags")
                    body = """{"models":[{"name":"translategemma:4b","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"name":"arbitrary:999b"}]}""";
                else if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/ps")
                    body = """{"models":[{"name":"translategemma:4b"}]}""";
                else if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/generate")
                {
                    var source = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using var json = JsonDocument.Parse(source);
                    Assert.AreEqual("translategemma:4b", json.RootElement.GetProperty("model").GetString());
                    Assert.IsFalse(json.RootElement.GetProperty("stream").GetBoolean());
                    var prompt = json.RootElement.GetProperty("prompt").GetString()!;
                    LastPrompt = prompt;
                    StringAssert.Contains(prompt, "Indonesian (id) to German (de) translator");
                    StringAssert.Contains(prompt, "into German:\n\n\nRina pulang.");
                    body = """{"response":"Rina ging nach Hause.","eval_count":20,"eval_duration":2000000000}""";
                }
                else throw new InvalidOperationException(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
