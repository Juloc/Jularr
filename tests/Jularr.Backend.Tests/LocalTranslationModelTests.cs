using System.Net;
using System.Text.Json;
using Jularr.Backend.Service.User.Translation.V1;
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
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.RunningAsync(server, default));
            var result = await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default);
            Assert.AreEqual("Rina ging nach Hause.", result.Text);
            Assert.AreEqual(10.0, result.TokensPerSecond);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "untrusted:90b", "id", "de", "Rina pulang.", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "translategemma:4b", "id", "de", new string('x', 2401), default));
        }

        [TestMethod]
        public void ProviderPaths_KeepDifferentModelsAndSourceLanguagesSeparate()
        {
            var first = LocalReadingPaths.Cache(12, "de", "translategemma:4b");
            var second = LocalReadingPaths.Cache(12, "de", "translategemma:12b");
            Assert.AreNotEqual(first, second);
            Assert.AreNotEqual(first, LocalReadingPaths.Cache(12, "id", "translategemma:4b"));
            Assert.AreNotEqual(first, Jularr.Backend.Logic.Reading.ReadingTranslations.Path(12, "de"));
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/tags")
                    body = """{"models":[{"name":"translategemma:4b"},{"name":"arbitrary:999b"}]}""";
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
