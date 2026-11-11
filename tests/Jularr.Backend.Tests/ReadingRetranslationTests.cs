using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Backend.Service.User.Translation.V1;
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
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);

            // The account's models, the one the Owner chose on the AI page first; Codex re-translates chapters in whole passages, Ollama block by block.
            CollectionAssert.AreEqual(new[] { Small + " False", Large + " False", "codex:gpt-test True", "codex:gpt-ok True" }, await ModelInfosAsync(host));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"CodexModel\" = 'gpt-ok' WHERE \"Id\" = 1");
            CollectionAssert.AreEqual(new[] { Small + " False", Large + " False", "codex:gpt-ok True", "codex:gpt-test True" }, await ModelInfosAsync(host));

            // A Media Manager lacks instance.settings.update: Codex is not offered.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            CollectionAssert.AreEqual(new[] { Small + " False", Large + " False" }, await ModelInfosAsync(host));
        }

        [TestMethod]
        public async Task Codex_WithoutAnAnsweringOllama_IsOnlyOfferedToTheOwner_AndNeverChosenOrAskedByItself()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            codex.SignIn();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => new AwayOllama()));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = '" + Small + "', \"CodexModel\" = 'gpt-ok', \"CodexEffort\" = 'low' WHERE \"Id\" = 1");
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);

            // Ollama does not answer: no model translates for anyone, and the Owner is offered Codex's model for a button press, not chosen as the default.
            var owner = await AvailabilityAsync(host);
            Assert.IsNull(owner.Model);
            Assert.IsTrue(owner.Away);
            Assert.AreEqual("codex:gpt-ok", owner.Codex);
            Assert.IsNull(owner.CodexProblem);

            // Reading what is stored costs Codex nothing; no request was made by the availability or the reads.
            using (var read = await host.SendAsync(HttpMethod.Get, Url(document, 0, "codex:gpt-ok") + "?sourceLanguage=en"))
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            Assert.IsFalse(File.Exists(Path.Combine(codex.Home, "asked")), "Codex must not be asked until a button is pressed.");

            // A Media Manager lacks the right: no Codex offer at all.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            var manager = await AvailabilityAsync(host);
            Assert.IsNull(manager.Codex);
            Assert.IsNull(manager.CodexProblem);
            using (var refused = await host.SendAsync(HttpMethod.Post, Url(document, 0, "codex:gpt-ok") + "/passages", new LocalReadingChunkDataV1("en", 0, [])))
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(codex.Home, "asked")));
        }

        [TestMethod]
        public async Task Codex_WithAnAnsweringOllama_IsOnlyOfferedBesideIt()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            codex.SignIn();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => new FakeOllama()));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);

            // Ollama translates for everyone as before; Codex is offered beside it and never replaces it.
            var local = await AvailabilityAsync(host);
            Assert.AreEqual(Large, local.Model);
            Assert.IsFalse(local.Away);
            Assert.IsNotNull(local.Codex);
            Assert.IsNull(local.CodexProblem);

            // An explicit default model of the admin-only provider never translates by itself either.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = '" + local.Codex + "' WHERE \"Id\" = 1");
            Assert.IsNull((await AvailabilityAsync(host)).Model);
        }

        [TestMethod]
        public async Task Codex_NotConnected_IsNotOffered_SoTheReaderStaysAsWithoutCodex()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => new AwayOllama()));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            var owner = await AvailabilityAsync(host);
            Assert.IsNull(owner.Model);
            Assert.IsNull(owner.Codex);
            Assert.IsNull(owner.CodexProblem);
            Assert.IsTrue(owner.Away);
        }

        [TestMethod]
        public async Task Chunks_TranslateTheChapterInSeveralRequests_FromTheBlockInView_HoldTheLanePerRequest_AndKeepWhatIsDone()
        {
            using var storage = new TemporaryDirectory();
            var chunky = new ChunkProvider();
            await using var host = await CreateHostAsync(storage.Path, new FakeOllama(), services => services.AddSingleton<ITranslationProvider>(chunky));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var lanes = host.Services.GetRequiredService<TranslationLanes>();
            chunky.Lanes = lanes;
            var url = Url(document, 0, ChunkProvider.Id) + "/passages";

            // The first request takes the blocks from the one in view onward; the lane is held for it and free after it.
            var chapter = new List<int>();
            var chunks = new List<LocalReadingChunkV1>();
            var from = 1;
            while (true)
            {
                var chunk = await PostChunkAsync(host, url, from, []);
                Assert.IsTrue(await IsFreeAsync(lanes, chunky.Key), "The lane is free between requests.");
                if (chunk.Blocks.Length == 0) break;
                chunks.Add(chunk);
                chapter.AddRange(chunk.Blocks.Select(block => block.Index));
                from = chunk.Blocks.Max(block => block.Index) + 1;
                Assert.IsTrue(chunk.Blocks.Length <= 2);
                Assert.IsTrue(chunk.Blocks.All(block => block.Text.StartsWith("Chunky: ", StringComparison.Ordinal)));
                if (chunk.Remaining == 0) break;
            }
            Assert.IsTrue(chunks.Count > 1, "The chapter needs several requests.");
            Assert.IsTrue(chapter[0] >= 1, "It starts at the block in view.");
            Assert.AreEqual(0, chapter[^1], "The start of the chapter follows last.");
            Assert.AreEqual(chapter.Count, chapter.Distinct().Count());
            Assert.AreEqual(chunky.Gates.Count, chunks.Count);
            Assert.IsTrue(chunky.Gates.All(held => held == 0), "The lane was held during every request.");
            Assert.AreEqual(0, chunks[^1].Remaining);
            Assert.IsTrue(chunks.All(chunk => chunk.Seconds >= 0 && chunk.Characters > 0));

            // Everything is stored: the section reads back with the chapter's texts, and asking again costs nothing.
            using (var read = await host.SendAsync(HttpMethod.Get, Url(document, 0, ChunkProvider.Id) + "?sourceLanguage=en"))
                Assert.AreEqual(chapter.Count, Texts((await read.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!).Count(text => !string.IsNullOrEmpty(text)));
            var calls = chunky.Calls;
            var again = await PostChunkAsync(host, url, 0, []);
            Assert.AreEqual(0, again.Blocks.Length);
            Assert.AreEqual(0, again.Remaining);
            Assert.AreEqual(calls, chunky.Calls);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\""));
        }

        [TestMethod]
        public async Task Chunks_AFailedRequest_NamesTheCause_KeepsTheFinishedOnes_AndTheRetryAsksOnlyForTheRest()
        {
            using var storage = new TemporaryDirectory();
            var chunky = new ChunkProvider();
            await using var host = await CreateHostAsync(storage.Path, new FakeOllama(), services => services.AddSingleton<ITranslationProvider>(chunky));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var lanes = host.Services.GetRequiredService<TranslationLanes>();
            chunky.Lanes = lanes;
            var url = Url(document, 0, ChunkProvider.Id) + "/passages";

            // A request breaks after its first block: that block is stored, the cause is the error code, and the lane is free.
            chunky.FailNext = "limit";
            using (var failed = await host.SendAsync(HttpMethod.Post, url, new LocalReadingChunkDataV1("en", 0, [])))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
                StringAssert.Contains(await failed.Content.ReadAsStringAsync(), "translation_limit_reached");
            }
            Assert.IsTrue(await IsFreeAsync(lanes, chunky.Key));
            int[] stored;
            using (var read = await host.SendAsync(HttpMethod.Get, Url(document, 0, ChunkProvider.Id) + "?sourceLanguage=en"))
                stored = [.. Texts((await read.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!).Select((text, index) => (text, index)).Where(item => !string.IsNullOrEmpty(item.text)).Select(item => item.index)];
            Assert.AreEqual(1, stored.Length, "What came before the break is kept.");

            // Other causes are named too (the request failing before any block).
            chunky.FailAt = 0;
            foreach (var (problem, code) in new[] { ("not_connected", "translation_not_connected"), ("", "translation_request_failed") })
            {
                chunky.FailNext = problem;
                using var failed = await host.SendAsync(HttpMethod.Post, url, new LocalReadingChunkDataV1("en", 0, []));
                StringAssert.Contains(await failed.Content.ReadAsStringAsync(), code);
            }
            chunky.FailAt = 1;

            // The retry from the start translates only what is missing.
            var asked = chunky.Asked.Count;
            var rest = new List<int>();
            while (await PostChunkAsync(host, url, 0, []) is { Blocks.Length: > 0 } chunk) rest.AddRange(chunk.Blocks.Select(block => block.Index));
            Assert.AreEqual(asked + rest.Count, chunky.Asked.Count);
            Assert.IsFalse(rest.Intersect(stored).Any(), "A finished block is never asked again.");

            // A block whose answer fails the checks even alone is reported and, passed as skipped, left out of the next request.
            chunky.RefuseBlock = true;
            await host.Fixture.RunSqlAsync("DELETE FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/chunky%'");
            var refused = await PostChunkAsync(host, url, 0, []);
            Assert.AreEqual(2, refused.Failed.Length);
            Assert.AreEqual(0, refused.Blocks.Length);
            var next = await PostChunkAsync(host, url, 0, refused.Failed);
            Assert.IsFalse(next.Failed.Intersect(refused.Failed).Any());
        }

        [TestMethod]
        public async Task Chunks_WhenTheirLaneIsTakenTooLong_AnswerBusy_WithoutAskingTheModel_AndRunWhileOllamaTranslates()
        {
            using var storage = new TemporaryDirectory();
            var chunky = new ChunkProvider();
            await using var host = await CreateHostAsync(storage.Path, new FakeOllama(), services => services.AddSingleton<ITranslationProvider>(chunky));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var lanes = host.Services.GetRequiredService<TranslationLanes>();
            lanes.ChunkWait = TimeSpan.FromMilliseconds(200);

            using (await lanes.TryEnterAsync(chunky.Key, TimeSpan.Zero, default))
            {
                using var busy = await host.SendAsync(HttpMethod.Post, Url(document, 0, ChunkProvider.Id) + "/passages", new LocalReadingChunkDataV1("en", 0, []));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                StringAssert.Contains(await busy.Content.ReadAsStringAsync(), "translation_local_busy");
            }
            Assert.AreEqual(0, chunky.Calls);

            // Ollama translating holds up no other provider: each has its own lane.
            using (await lanes.TryEnterAsync(OllamaTranslationProvider.ProviderKey, TimeSpan.Zero, default))
            {
                var chunk = await PostChunkAsync(host, Url(document, 0, ChunkProvider.Id) + "/passages", 0, []);
                Assert.IsTrue(chunk.Blocks.Length > 0);
            }
        }

        private static async Task<bool> IsFreeAsync(TranslationLanes lanes, string key)
        {
            using var hold = await lanes.TryEnterAsync(key, TimeSpan.Zero, default);
            return hold is not null;
        }

        [TestMethod]
        public void Chunk_StartsAtTheBlockInView_WrapsToTheStart_AndRespectsBothLimits()
        {
            var batch = new ChunkProvider { PerRequest = 5 };
            List<(int Index, string Text)> pending = [.. Enumerable.Range(0, 10).Select(index => (index, new string('x', 100)))];
            CollectionAssert.AreEqual(new[] { 7, 8, 9, 0, 1 }, PassageTranslation.Chunk(pending, 7, batch).Select(item => item.Index).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, PassageTranslation.Chunk(pending, 0, batch).Select(item => item.Index).ToArray());
            CollectionAssert.AreEqual(new[] { 9 }, PassageTranslation.Chunk([(9, "y")], 20, batch).Select(item => item.Index).ToArray());

            // The characters limit ends a request early, but one block always goes through, however long.
            batch.Characters = 250;
            CollectionAssert.AreEqual(new[] { 3, 4 }, PassageTranslation.Chunk(pending, 3, batch).Select(item => item.Index).ToArray());
            CollectionAssert.AreEqual(new[] { 0 }, PassageTranslation.Chunk([(0, new string('x', 900))], 0, batch).Select(item => item.Index).ToArray());
        }

        private static async Task<LocalReadingChunkV1> PostChunkAsync(Host host, string url, int from, int[] skip)
        {
            using var response = await host.SendAsync(HttpMethod.Post, url, new LocalReadingChunkDataV1("en", from, skip));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalReadingChunkV1>(JsonSerializerOptions.Web))!;
        }

        private static async Task<LocalTranslationAvailabilityV1> AvailabilityAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/local/availability");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalTranslationAvailabilityV1>(JsonSerializerOptions.Web))!;
        }

        // An Ollama machine that is set up but does not answer.
        private sealed class AwayOllama : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("Connection refused");
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

        // An admin-only provider that translates in several requests of two blocks each, for the chunked button translation; it can fail on demand.
        private sealed class ChunkProvider : IBatchTranslationProvider
        {
            public const string Id = "chunky:model";
            public int PerRequest { get; init; } = 2;
            private int _calls;
            private string? _problem;

            public TranslationLanes? Lanes { get; set; }
            public List<int> Gates { get; } = [];
            public List<string> Asked { get; } = [];
            public string? FailNext { get; set; }
            public int FailAt { get; set; } = 1;
            public bool RefuseBlock { get; set; }
            public int Characters { get; set; } = 6000;
            public int Calls => _calls;

            public string Key => "chunky";
            public IReadOnlyList<string> Models => ["model"];
            public bool IsAdminOnly => true;
            public int BatchCharacters => Characters;
            public int BatchBlocks => PerRequest;

            public (string? Model, string? Problem) Offer(TranslationSetupV1 setup) => ("model", _problem);

            public IReadOnlyList<string> Available(TranslationSetupV1 setup) => Models;

            public TranslationMachine? Choose(TranslationSetupV1 setup, string? model) => model == "model" ? new TranslationMachine(this, model, new Uri("chunky://local/")) : null;

            public Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct) => Task.FromResult(Choose(setup, model));

            public Task<string> DigestAsync(TranslationMachine machine, CancellationToken ct) => Task.FromResult("chunky-1");

            public Task<string?[]> TranslateBlocksAsync(TranslationMachine machine, string source, string target, IReadOnlyList<string> blocks, string? context, CancellationToken ct) =>
                TranslateBlocksStreamAsync(machine, source, target, blocks, context, (_, _) => { }, ct);

            // A block comes out as soon as it is answered; with FailNext set the request fails at block FailAt of the request (after the first one: a stream that breaks).
            public async Task<string?[]> TranslateBlocksStreamAsync(TranslationMachine machine, string source, string target, IReadOnlyList<string> blocks, string? context, Action<int, string> onBlock,
                CancellationToken ct)
            {
                _calls++;
                Gates.Add(Lanes is null ? -1 : await IsFreeAsync(Lanes, Key) ? 1 : 0);
                var answers = new string?[blocks.Count];
                for (var index = 0; index < blocks.Count; index++)
                {
                    if (FailNext is { } problem && index == FailAt)
                    {
                        FailNext = null;
                        _problem = problem.Length == 0 ? null : problem;
                        throw new InvalidDataException("The model failed.");
                    }

                    if (RefuseBlock) continue;
                    answers[index] = "Chunky: " + blocks[index];
                    Asked.Add(blocks[index]);
                    onBlock(index, answers[index]!);
                }

                _problem = null;
                return answers;
            }

            public Task<string?> TranslateBlockAsync(TranslationMachine machine, string source, string target, string text, string? context, CancellationToken ct) =>
                Task.FromResult<string?>(RefuseBlock ? null : "Chunky: " + text);

            public IDisposable BeginChapter(TranslationMachine machine, int chapter, string? title) => new Scope();

            public Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct) => Task.FromResult(text);

            public void Fail(TranslationMachine machine)
            {
            }

            private sealed class Scope : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        // A provider only administrators may choose, like the Owner's ChatGPT account.
        private sealed class AdminOnlyProvider : ITranslationProvider
        {
            public const string Id = "restricted:model";

            public string Key => "restricted";
            public IReadOnlyList<string> Models => ["model"];
            public bool IsAdminOnly => true;

            public IReadOnlyList<string> Available(TranslationSetupV1 setup) => Models;

            public TranslationMachine? Choose(TranslationSetupV1 setup, string? model) => model == "model" ? new TranslationMachine(this, model, new Uri("restricted://local/")) : null;

            public Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct) => Task.FromResult(Choose(setup, model));

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
