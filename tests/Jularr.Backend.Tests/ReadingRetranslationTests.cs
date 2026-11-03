using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // A reader re-translates a paragraph, a chapter or the whole book with another model; that model's text wins only there, for every reader.
    [TestClass]
    public sealed class ReadingRetranslationTests
    {
        private const string Small = "ollama:translategemma:4b";
        private const string Large = "ollama:translategemma:12b";

        [TestMethod]
        public async Task Paragraph_ReTranslatedWithAnotherModel_WinsOnlyThere_ForEveryReader_AndTheEarlierTextStaysUntilItIsDone()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var document = await PrepareAsync(host, storage.Path);
            var before = await ReadSectionAsync(host, document, 0);
            var block = Array.FindIndex(Texts(before), text => !string.IsNullOrEmpty(text));

            // The choice is kept first: until the model has translated the paragraph, it shows the text it had.
            using (var chosen = await ChooseAsync(host, document, 0, Large, 1, block))
                Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            var waiting = await ReadSectionAsync(host, document, 0);
            Assert.AreEqual(before.Blocks, waiting.Blocks);
            Assert.AreEqual(Small, waiting.Models[block]);

            using (var generated = await host.SendAsync(HttpMethod.Post, BlockUrl(document, 0, Large, block), new LocalReadingBlockDataV1("en")))
            {
                Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode, await generated.Content.ReadAsStringAsync());
                StringAssert.StartsWith((await generated.Content.ReadFromJsonAsync<LocalReadingBlockV1>())!.Text, "LARGE ");
            }
            var mixed = await ReadSectionAsync(host, document, 0);
            var texts = Texts(mixed);
            StringAssert.StartsWith(texts[block], "LARGE ");
            Assert.AreEqual(Large, mixed.Models[block]);
            Assert.IsTrue(texts.Where((text, index) => index != block && !string.IsNullOrEmpty(text)).All(text => text!.StartsWith("SMALL ", StringComparison.Ordinal)), mixed.Blocks);
            Assert.IsTrue(mixed.Models.Where((model, index) => index != block && model is not null).All(model => model == Small));
            Assert.IsTrue((await ReadSectionAsync(host, document, 1)).Models.All(model => model is null || model == Small), "another chapter keeps its own model");

            // Another profile reads the same choice.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            var other = await ReadSectionAsync(host, document, 0);
            Assert.AreEqual(mixed.Blocks, other.Blocks);
            Assert.AreEqual(Large, other.Models[block]);
        }

        [TestMethod]
        public async Task SectionAndBook_ReTranslated_ChooseEveryBlock_AndTheJobTranslatesTheRestWithTheChosenModel()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var document = await PrepareAsync(host, storage.Path);
            var sections = Convert.ToInt32(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + document), System.Globalization.CultureInfo.InvariantCulture);
            var blocks = Convert.ToInt64(await host.ScalarAsync("SELECT (\"Structure\" -> 'Sections' -> 0 ->> 'Blocks')::bigint FROM \"ReadingDocuments\" WHERE \"Id\" = " + document), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(sections > 1 && blocks > 1);

            // A chapter: every block of it, nothing else; the earlier text stays until the model has translated each.
            using (var chosen = await ChooseAsync(host, document, 0, Large, 2, null))
                Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            Assert.AreEqual(blocks, await host.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\" WHERE \"Model\" = '" + Large + "' AND \"SectionIndex\" = 0"));
            Assert.AreEqual(blocks, await host.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\""));
            Assert.IsTrue(Texts(await ReadSectionAsync(host, document, 0)).All(text => string.IsNullOrEmpty(text) || text.StartsWith("SMALL ", StringComparison.Ordinal)));

            // The whole book: every block of every chapter, and the background job translates them with the chosen model.
            using (var chosen = await ChooseAsync(host, document, 0, Large, 3, null))
                Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            Assert.AreEqual((long)sections, await host.ScalarAsync("SELECT count(DISTINCT \"SectionIndex\") FROM \"ReadingTranslationChoices\" WHERE \"Model\" = '" + Large + "'"));
            var small = ollama.Generations(Small);
            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await TranslateAsync(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            }
            Assert.IsTrue(ollama.Generations(Large) > 0);
            Assert.AreEqual(small, ollama.Generations(Small), "the default model has nothing left to do");
            for (var section = 0; section < sections; section++)
            {
                var read = await ReadSectionAsync(host, document, section);
                Assert.IsTrue(Texts(read).All(text => string.IsNullOrEmpty(text) || text.StartsWith("LARGE ", StringComparison.Ordinal)), read.Blocks);
                Assert.IsTrue(read.Models.All(model => model is null || model == Large));
            }

            // The default model's translation stays where it was, for the day the choice changes back.
            Assert.AreEqual((long)sections, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + document));
        }

        [TestMethod]
        public async Task Models_AnAdminOnlyProvider_IsOfferedAndUsableOnlyWithTheRight()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama, services => services.AddSingleton<ITranslationProvider, AdminOnlyProvider>());
            var document = await PrepareAsync(host, storage.Path);

            // The Owner (who holds instance.settings.update) sees every model a machine has, the admin-only provider's too, and may translate with it.
            CollectionAssert.AreEqual(new[] { Small, Large, AdminOnlyProvider.Id }, await ModelsAsync(host));
            using (var chosen = await ChooseAsync(host, document, 0, AdminOnlyProvider.Id, 1, 0))
                Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());

            // An account without that right is neither offered nor allowed it.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            CollectionAssert.AreEqual(new[] { Small, Large }, await ModelsAsync(host));
            using (var refused = await ChooseAsync(host, document, 0, AdminOnlyProvider.Id, 2, null))
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            using (var refused = await host.SendAsync(HttpMethod.Post, BlockUrl(document, 0, AdminOnlyProvider.Id, 0), new LocalReadingBlockDataV1("en")))
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            using (var allowed = await ChooseAsync(host, document, 0, Large, 1, 0))
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());

            // The Owner's default model translates for everyone, whichever provider it is from.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = '" + AdminOnlyProvider.Id + "' WHERE \"Id\" = 1");
            using (var asDefault = await host.SendAsync(HttpMethod.Post, BlockUrl(document, 0, AdminOnlyProvider.Id, 0), new LocalReadingBlockDataV1("en")))
                Assert.AreEqual(HttpStatusCode.OK, asDefault.StatusCode, await asDefault.Content.ReadAsStringAsync());

            // A block that does not exist, an unknown model, or the module off.
            using (var missing = await ChooseAsync(host, document, 0, Large, 1, 100_000))
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using (var unknown = await ChooseAsync(host, document, 0, "ollama:untrusted:90b", 1, 0))
                Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Translation'");
            using (var off = await ChooseAsync(host, document, 0, Large, 1, 0))
                Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }

        [TestMethod]
        public async Task Models_ConnectedCodex_OffersTheOwnersModelForWholePassages_OnlyWithTheRight()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            codex.SignIn();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            // The account's first model, else the one the Owner chose on the AI page; Codex re-translates chapters in whole passages, Ollama block by block.
            CollectionAssert.AreEqual(new[] { Small + " False", Large + " False", "codex:gpt-test True" }, await ModelInfosAsync(host));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"CodexModel\" = 'gpt-ok' WHERE \"Id\" = 1");
            CollectionAssert.Contains(await ModelInfosAsync(host), "codex:gpt-ok True");

            // A Media Manager lacks instance.settings.update: Codex is not offered.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            CollectionAssert.AreEqual(new[] { Small + " False", Large + " False" }, await ModelInfosAsync(host));
        }

        private static async Task<string[]> ModelInfosAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/local/models");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return [.. (await response.Content.ReadFromJsonAsync<ReaderTranslationModelsV1>(JsonSerializerOptions.Web))!.Models.Select(model => model.Id + " " + model.Passages)];
        }

        private static Task<Host> CreateHostAsync(string storage, FakeOllama ollama, Action<IServiceCollection>? more = null) =>
            LibraryTests.CreateHostAsync(storage, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services =>
                {
                    services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama);
                    more?.Invoke(services);
                });

        // An English book that every profile reads in German, translated whole by the default model (the small one).
        private static async Task<long> PrepareAsync(Host host, string storage)
        {
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = '" + Small + "' WHERE \"Id\" = 1");
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await TranslateAsync(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            return document;
        }

        private static string Url(long document, int section, string model) =>
            "/api/v1/reading/documents/" + document + "/sections/" + section + "/local-translations/de/" + model;

        private static string BlockUrl(long document, int section, string model, int block) => Url(document, section, model) + "/blocks/" + block;

        private static Task<HttpResponseMessage> ChooseAsync(Host host, long document, int section, string model, int scope, int? block) =>
            host.SendAsync(HttpMethod.Put, Url(document, section, model) + "/choices", new { scope, blockIndex = block });

        private static async Task<LocalReadingTranslationV1> ReadSectionAsync(Host host, long document, int section)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Url(document, section, Small) + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
        }

        private static string?[] Texts(LocalReadingTranslationV1 read) => JsonSerializer.Deserialize<string?[]>(read.Blocks)!;

        private static async Task<string[]> ModelsAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/local/models");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return [.. (await response.Content.ReadFromJsonAsync<ReaderTranslationModelsV1>(JsonSerializerOptions.Web))!.Models.Select(model => model.Id)];
        }

        private static async Task<bool> TranslateAsync(AsyncServiceScope scope) =>
            (await scope.ServiceProvider.GetRequiredService<TranslateReadingBooksV1>().ExecuteAsync<UpdateResult>(new BookTranslationParametersV1(), default, CancellationToken.None)).Success;

        // Two TranslateGemma models; each answers with its size before the block's text.
        private sealed class FakeOllama : HttpMessageHandler
        {
            private readonly Dictionary<string, int> _generations = new(StringComparer.Ordinal);

            public int Generations(string model)
            {
                lock (_generations) return _generations.GetValueOrDefault(model[(model.IndexOf(':', StringComparison.Ordinal) + 1)..]);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = new string('a', 64) }, new { name = "translategemma:12b", digest = new string('c', 64) } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var model = input.RootElement.GetProperty("model").GetString()!;
                    var prompt = input.RootElement.GetProperty("prompt").GetString()!;
                    lock (_generations) _generations[model] = _generations.GetValueOrDefault(model) + 1;
                    var answer = (model == "translategemma:12b" ? "LARGE " : "SMALL ") + prompt[(prompt.LastIndexOf('\n') + 1)..];
                    body = JsonSerializer.Serialize(new { response = answer, eval_count = 2, eval_duration = 200000000 });
                }
                else throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }

        // A provider only administrators may choose, like the Owner's ChatGPT account.
        private sealed class AdminOnlyProvider : ITranslationProvider
        {
            public const string Id = "restricted:model";

            public string Key => "restricted";
            public IReadOnlyList<string> Models => ["model"];
            public bool IsAdminOnly => true;

            public Task<IReadOnlyList<string>> AvailableAsync(TranslationSetupV1 setup, CancellationToken ct) => Task.FromResult(Models);

            public Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct) =>
                Task.FromResult(model == "model" ? new TranslationMachine(this, model, new Uri("restricted://local/")) : null);

            public Task<string> DigestAsync(TranslationMachine machine, CancellationToken ct) => Task.FromResult("restricted-1");

            public Task<string?> TranslateBlockAsync(TranslationMachine machine, string source, string target, string text, string? context, CancellationToken ct) =>
                Task.FromResult<string?>("RESTRICTED " + text);

            public Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct) => Task.FromResult(text);

            public void Fail(TranslationMachine machine)
            {
            }
        }
    }
}
