using System.Net;
using System.Text.Json;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using Jularr.Integrations.Logic.Translation;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LocalTranslationModelTests
    {
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
            using var http = new HttpClient(new FakeOllama());
            var client = new OllamaLocalModels(http);
            var server = new Uri("http://ollama:11434/");
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.InstalledAsync(server, default));
            Assert.AreEqual(new string('a', 64), await client.DigestAsync(server, "translategemma:4b", default));
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.RunningAsync(server, default));
            var result = await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default);
            Assert.AreEqual("Rina ging nach Hause.", result.Text);
            Assert.AreEqual(10.0, result.TokensPerSecond);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "untrusted:90b", "id", "de", "Rina pulang.", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "translategemma:4b", "id", "de", new string('x', 20001), default));
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
            Assert.IsFalse(noCapacity.Queue("translategemma:4b"));
            Assert.IsFalse(noCapacity.Queue("other:27b"));

            using var adequate = new LocalTranslationDownloads(client,
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L }),
                Options.Create(new MediaOptions { TranslationPath = Path.Combine(root, "approved") }),
                new LocalTranslationGate());
            Assert.IsTrue(adequate.Queue("translategemma:4b"));
            Assert.IsFalse(adequate.Queue("translategemma:12b"));
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
                    Assert.IsTrue(beforeRestart.Queue("translategemma:4b"));
                    Assert.AreEqual("queued", beforeRestart.Current().State);
                    Assert.IsTrue(File.Exists(Path.Combine(root, "ollama-pull-state.json")));
                }
                // No automatic network activity: restart reports interruption and the owner
                // explicitly retries the same model, whose partial blobs Ollama retains.
                using (var afterRestart = new LocalTranslationDownloads(client, settings, media, new LocalTranslationGate()))
                {
                    Assert.AreEqual("interrupted", afterRestart.Current().State);
                    Assert.AreEqual("translategemma:4b", afterRestart.Current().Model);
                    Assert.IsTrue(afterRestart.Queue("translategemma:4b"));
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
                Assert.IsFalse(downloads.Queue("translategemma:4b"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
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
