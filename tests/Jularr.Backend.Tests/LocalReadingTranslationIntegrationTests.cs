using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Integrations.Logic.Translation;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LocalReadingTranslationIntegrationTests
    {
        [TestMethod]
        public async Task Reader_LocalModelCachesAuthorizedBlocksWithoutInventingEpubLanguage()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            var mozilla = new UnavailableMozilla();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services =>
                {
                    services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama);
                    services.AddHttpClient<MozillaTranslationsClient>().ConfigurePrimaryHttpMessageHandler(() => mozilla);
                });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await CheckMachinesAsync(host);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            var document = (await opened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!;
            using var reader = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            Assert.AreEqual(HttpStatusCode.OK, reader.StatusCode, await reader.Content.ReadAsStringAsync());
            var readerHtml = await reader.Content.ReadAsStringAsync();
            StringAssert.Contains(readerHtml, "data-language-choice=" + (char)34 + "de" + (char)34);
            StringAssert.Contains(readerHtml, "data-local-model=" + (char)34 + "ollama:translategemma:4b" + (char)34);
            StringAssert.Contains(readerHtml, "reader__footer");
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "seek" + (char)34);
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "turn-tools" + (char)34);
            // The administration view must expose model management and the live status
            // without changing the ordinary Reader's permissions.
            using var adminPage = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            Assert.AreEqual(HttpStatusCode.OK, adminPage.StatusCode, await adminPage.Content.ReadAsStringAsync());
            var adminHtml = await adminPage.Content.ReadAsStringAsync();
            StringAssert.Contains(adminHtml, "data-translation-download");
            StringAssert.Contains(adminHtml, "translategemma:4b");

            ollama.Unavailable = true;
            await CheckMachinesAsync(host);
            using var offlineAdmin = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            Assert.AreEqual(HttpStatusCode.OK, offlineAdmin.StatusCode, await offlineAdmin.Content.ReadAsStringAsync());
            var offlineHtml = await offlineAdmin.Content.ReadAsStringAsync();
            StringAssert.Contains(offlineHtml, "data-translation-download");
            StringAssert.Contains(offlineHtml, "translategemma:4b", "Model choices must stay visible when Ollama is offline.");
            ollama.Unavailable = false;
            await CheckMachinesAsync(host);

            // The server's TranslateGemma is the Reader's only translator; Mozilla is neither asked nor offered (#1726).
            StringAssert.Contains(readerHtml, "data-translation-engine=" + (char)34 + "gemma" + (char)34);
            Assert.IsFalse(readerHtml.Contains("data-local-quality-toggle", StringComparison.Ordinal));
            Assert.IsFalse(readerHtml.Contains("data-route-url", StringComparison.Ordinal));
            Assert.AreEqual(0, mozilla.Requests, "Opening the Reader must not reach Mozilla.");

            var baseUrl = "/api/v1/reading/documents/" + document.DocumentId + "/sections/0/local-translations/de/ollama:translategemma:4b";
            // A model id without its provider is no model.
            using var bare = await host.SendAsync(HttpMethod.Get, baseUrl.Replace("ollama:", "", StringComparison.Ordinal) + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.BadRequest, bare.StatusCode);
            var request = baseUrl + "/blocks/0";

            using var before = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, before.StatusCode, await before.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>((await before.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Blocks));

            using var generated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode, await generated.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", (await generated.Content.ReadFromJsonAsync<LocalReadingBlockV1>())!.Text);
            Assert.AreEqual(1, ollama.GenerationCount);

            using var cached = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, cached.StatusCode, await cached.Content.ReadAsStringAsync());
            Assert.AreEqual(1, ollama.GenerationCount, "The cached block should not be regenerated.");
            using var shared = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode, await shared.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", JsonSerializer.Deserialize<string[]>((await shared.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Blocks)![0]);

            using var reopened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
            Assert.IsFalse((await reopened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!.TranslatedLanguages.Contains("local-v1"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%'"));

            // A new version of the model, found by the next check: the earlier text stays visible, marked stale, until this version has replaced it.
            ollama.Digest = new string('b', 64);
            await CheckMachinesAsync(host);
            using var stale = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
            var staleRead = (await stale.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
            Assert.AreEqual("Kapitel Eins", JsonSerializer.Deserialize<string?[]>(staleRead.Blocks)![0]);
            Assert.AreEqual(("ollama:translategemma:4b", true), (staleRead.Models[0], staleRead.Stale[0]));
            using var regenerated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, regenerated.StatusCode, await regenerated.Content.ReadAsStringAsync());
            Assert.AreEqual(2, ollama.GenerationCount);
            using var renewed = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.IsFalse((await renewed.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Stale[0]);

            // A block someone chose another model for shows that model's text, for every reader; the other blocks keep the default's.
            await host.Fixture.RunSqlAsync($$"""
                INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                SELECT resource."ReadingDocumentId", ':localtranslation/v4/translategemma:12b/de/00000', resource."MediaType",
                    convert_to(replace(convert_from(resource."Bytes", 'UTF8'), 'Kapitel Eins', 'Erstes Kapitel'), 'UTF8')
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = {{document.DocumentId}} AND resource."Path" = ':localtranslation/v4/translategemma:4b/de/00000';
                INSERT INTO "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                VALUES ({{document.DocumentId}}, 0, 'de', 0, 'ollama:translategemma:12b'), ({{document.DocumentId}}, 0, 'fr', 1, 'ollama:translategemma:12b');
                """);
            using var mixed = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, mixed.StatusCode, await mixed.Content.ReadAsStringAsync());
            var mixedRead = (await mixed.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
            var mixedBlocks = JsonSerializer.Deserialize<string?[]>(mixedRead.Blocks)!;
            Assert.AreEqual(("Erstes Kapitel", "ollama:translategemma:12b", false), (mixedBlocks[0], mixedRead.Models[0], mixedRead.Stale[0]));
            Assert.IsTrue(mixedBlocks.Length > 1 && mixedBlocks.Skip(1).All(block => block is null) && mixedRead.Models.Skip(1).All(model => model is null), mixedRead.Blocks);

            // The order's first machine answers but fails the block: the same request takes it to the next machine at once.
            ollama.FailingHost = "10.9.9.9";
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationMachines\" = '{http://10.9.9.9:11434/,http://ollama:11434/}'");
            using var passedOn = await host.SendAsync(HttpMethod.Post, baseUrl.Replace("/de/", "/fr/", StringComparison.Ordinal) + "/blocks/0", new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, passedOn.StatusCode, await passedOn.Content.ReadAsStringAsync());
            Assert.AreEqual(3, ollama.GenerationCount);
            Assert.AreEqual(1, ollama.FailedGenerations);

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Translation'");
            using var unavailable = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.Forbidden, unavailable.StatusCode);
        }

        [TestMethod]
        public async Task DefaultModel_TheOwnerChoosesAnInstalledModel_AndEveryServerTranslationFollowsIt()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await CheckMachinesAsync(host);

            // Nothing chosen: the largest installed TranslateGemma, offered on the setup tab among the installed models.
            Assert.AreEqual("ollama:translategemma:4b", await AvailableAsync(host));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "?handler=Model");
            StringAssert.Contains(html, "value=" + (char)34 + "ollama:translategemma:4b" + (char)34);

            // A model no machine has translates nothing rather than another model; an id without provider or an unapproved model is refused.
            using (var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1("ollama:translategemma:12b")))
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.IsNull(await AvailableAsync(host));
            using (var models = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/models"))
            {
                var read = (await models.Content.ReadFromJsonAsync<LocalTranslationModelsV1>(JsonSerializerOptions.Web))!;
                Assert.AreEqual("ollama:translategemma:12b", read.DefaultModel);
                CollectionAssert.AreEqual(new[] { "ollama:translategemma:4b" }, read.Choices);
            }
            foreach (var refused in new[] { "translategemma:4b", "ollama:untrusted:90b", "codex:gpt-5" })
            {
                using var response = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1(refused));
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, refused);
            }

            using (var installed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1("ollama:translategemma:4b")))
                Assert.AreEqual(HttpStatusCode.OK, installed.StatusCode);
            Assert.AreEqual("ollama:translategemma:4b", await AvailableAsync(host));
            using (var automatic = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1(null)))
                Assert.AreEqual(HttpStatusCode.OK, automatic.StatusCode);
            Assert.IsNull(await host.ScalarAsync("SELECT \"TranslationModel\" FROM \"InstanceSettings\" WHERE \"Id\" = 1") as string);
        }

        [TestMethod]
        public async Task TranslationPage_AndReader_ReadTheLastCheck_AndAskNoMachine_EvenOneThatNeverAnswers()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama)
                    .AddTypedClient(http => new OllamaLocalModels(http) { QuestionLimit = TimeSpan.FromMilliseconds(300) }));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            const string setup = "/Admin/Instance/Translation?tab=setup";

            // Before the first check the page says so, and opening it asked nobody.
            StringAssert.Contains(await HtmlAsync(host, setup), "Not checked yet");
            Assert.AreEqual(0, ollama.Requests);

            // The machine stops answering: the next check keeps it as not reachable for lack of an answer, with the time of the check.
            ollama.Silent = true;
            await CheckMachinesAsync(host);
            var html = await HtmlAsync(host, setup);
            StringAssert.Contains(html, "not reachable");
            StringAssert.Contains(html, "No answer in time");
            StringAssert.Contains(html, "Checked at");
            StringAssert.Contains(html, "?handler=Check");

            // The translation page, its models and the Reader read that check: no question to the silent machine, and no waiting for it.
            var asked = ollama.Requests;
            foreach (var path in new[] { setup, "/Admin/Instance/Translation", "/Admin/Instance/Translation?handler=Progress", "/api/v1/admin/translation/models", "/api/v1/admin/translation/progress",
                "/api/v1/translation/local/availability", "/api/v1/translation/local/models" })
            {
                using (var warm = await host.SendAsync(HttpMethod.Get, path)) Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode, path + ": " + await warm.Content.ReadAsStringAsync());
                var watch = Stopwatch.StartNew();
                using var response = await host.SendAsync(HttpMethod.Get, path);
                watch.Stop();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
                Console.WriteLine(path + ": " + watch.Elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(1), path + " took " + watch.Elapsed);
            }

            Assert.AreEqual(asked, ollama.Requests);

            // Check now on the page asks again: back, the machine translates for everyone.
            ollama.Silent = false;
            var csrf = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            using (var checkedNow = await host.SendAsync(HttpMethod.Post, "/Admin/Instance/Translation?handler=Check", new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }))
                Assert.AreEqual(HttpStatusCode.Redirect, checkedNow.StatusCode, await checkedNow.Content.ReadAsStringAsync());
            Assert.AreEqual("ollama:translategemma:4b", await AvailableAsync(host));
            Assert.IsFalse((await HtmlAsync(host, setup)).Contains("not reachable", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task MachineWorker_ChecksEveryThirtySeconds_SoAMachineThatIsBackTranslatesAgain()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            var time = new WorkerJobTests.ManualTime(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services =>
                {
                    services.AddSingleton<TimeProvider>(time);
                    services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama);
                });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var machines = host.Services.GetRequiredService<LocalTranslationMachines>();
            var providers = host.Services.GetRequiredService<TranslationProviders>();
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            var server = new Uri("http://ollama:11434/");
            var setup = new TranslationSetupV1([], null);
            using var worker = new TranslationMachineWorker(time, jobs, host.Services.GetRequiredService<WorkerJobRunner>());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                // Shortly after the start the machines are checked once.
                await time.WaitForTimerAsync(WorkerJobs.Definition("translationMachines").StartDelay);
                time.Advance(WorkerJobs.Definition("translationMachines").StartDelay);
                await CheckedAsync(machines, jobs, server, time.Now);
                Assert.AreEqual("ollama:translategemma:4b", providers.Choose(setup)?.ModelId);

                // The first pass is kept before the clock moves on (later ones are kept once a minute).
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (await host.ScalarAsync("SELECT \"LastFinishedAt\" FROM \"WorkerJobs\" WHERE \"Key\" = 'translationMachines'") is not DateTime kept || kept.Ticks != time.Now.UtcTicks)
                {
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The first pass was not kept.");
                    await Task.Delay(10);
                }

                // A machine that goes away is found by the next tick; the book job passes it over.
                ollama.Unavailable = true;
                time.Advance(TranslationMachineWorker.Tick - WorkerJobs.Definition("translationMachines").StartDelay);
                await CheckedAsync(machines, jobs, server, time.Now);
                Assert.IsNull(machines.CheckOf(server)?.Installed);
                Assert.IsNull(await providers.ChooseAsync(setup, CancellationToken.None));

                // Back again, it translates after the next tick, thirty seconds later at most.
                ollama.Unavailable = false;
                time.Advance(TranslationMachineWorker.Tick);
                await CheckedAsync(machines, jobs, server, time.Now);
                Assert.AreEqual("ollama:translategemma:4b", (await providers.ChooseAsync(setup, CancellationToken.None))?.ModelId);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [TestMethod]
        public async Task Download_UnderWay_HoldsUpNoTranslation()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["LocalTranslation:OperatorVerifiedFreeBytes"] = "6000000000" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await CheckMachinesAsync(host);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = (await ReadingTests.OpenAsync(host, first)).DocumentId;

            // A model download is under way on the machine and sends nothing.
            using (var pull = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/translation/machines/0/models/translategemma:4b/pull"))
                Assert.AreEqual(HttpStatusCode.OK, pull.StatusCode, await pull.Content.ReadAsStringAsync());
            await ollama.Pulling.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The Reader's block is translated meanwhile.
            using var generated = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/documents/" + document + "/sections/0/local-translations/de/ollama:translategemma:4b/blocks/0",
                new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode, await generated.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", (await generated.Content.ReadFromJsonAsync<LocalReadingBlockV1>())!.Text);
            Assert.AreEqual("downloading", host.Services.GetRequiredService<LocalTranslationDownloads>().Current().State);
        }

        [TestMethod]
        public async Task Cache_OtherSectionsAreWrittenMeanwhile_TheSameSectionWaits_AndKeepsTheBlocksOfBoth()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var documentId = await OpenBookAsync(host, storage.Path);
            var first = LocalReadingPaths.Cache(0, "de", "ollama:translategemma:4b");

            // The background job writes the first section and has not committed yet.
            await using var job = await BeginWriterAsync(host);
            await StoreAsync(job, documentId, first, 0, "Eins");

            // The Reader writes another section of the same book at the same time, without waiting for the job.
            await using (var reader = await BeginWriterAsync(host))
            {
                await StoreAsync(reader, documentId, LocalReadingPaths.Cache(1, "de", "ollama:translategemma:4b"), 0, "Eins");
                await reader.CommitAsync();
            }

            // Codex writes another block of the first section: it waits for the job and then adds its block to the job's, so neither is lost.
            await using var codex = await BeginWriterAsync(host);
            var waiting = StoreAsync(codex, documentId, first, 1, "Zwei");
            await UntilWaitingAsync(host);
            Assert.IsFalse(waiting.IsCompleted);
            await job.CommitAsync();
            CollectionAssert.AreEqual(new[] { "Eins", "Zwei" }, await waiting);
            await codex.CommitAsync();
            var stored = (string)(await host.ScalarAsync($"SELECT convert_from(\"Bytes\", 'UTF8') FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = {documentId} AND \"Path\" = '{first}'"))!;
            CollectionAssert.AreEqual(new[] { "Eins", "Zwei" }, LocalReadingPaths.Parse(stored, SectionHtml, "en", Digest, 2)!.Texts);
        }

        [TestMethod]
        public async Task Cache_PreparingTheBookAgainAndDeletingItsTranslation_WaitForATranslationBeingWritten()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var (asset, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var documentId = await OpenAsync(host, asset);
            var path = LocalReadingPaths.Cache(0, "de", "ollama:translategemma:4b");

            // Deleting the language waits for the translation being written, and then removes it as well.
            await using (var writer = await BeginWriterAsync(host))
            {
                await StoreAsync(writer, documentId, path, 0, "Eins");
                var deleting = host.SendAsync(HttpMethod.Delete, "/api/v1/admin/library/documents/" + documentId + "/translations/de", new DeleteTranslationDataV1(true));
                await UntilWaitingAsync(host);
                Assert.IsFalse(deleting.IsCompleted);
                await writer.CommitAsync();
                using var deleted = await deleting.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/%'"));

            // An interrupted preparation starts over once the translation being written has landed, and clears it with the rest.
            await host.Fixture.RunSqlAsync($"UPDATE \"ReadingDocuments\" SET \"CacheCompletedAt\" = NULL WHERE \"Id\" = {documentId}");
            await using (var writer = await BeginWriterAsync(host))
            {
                await StoreAsync(writer, documentId, path, 0, "Eins");
                var opening = OpenAsync(host, asset);
                await UntilWaitingAsync(host);
                Assert.IsFalse(opening.IsCompleted);
                await writer.CommitAsync();
                Assert.AreEqual(documentId, await opening.WaitAsync(TimeSpan.FromSeconds(30)));
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/%'"));
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"CacheCompletedAt\" IS NOT NULL FROM \"ReadingDocuments\" WHERE \"Id\" = {documentId}"));
        }

        private const string SectionHtml = "<p data-b=\"0\">One</p><p data-b=\"1\">Two</p>";
        private static readonly string Digest = new('a', 64);

        // A writer's own transaction; one that waits too long fails the test instead of holding it.
        private static async Task<SqlContext> BeginWriterAsync(Host host)
        {
            var sql = new SqlContext(host.Fixture.Database);
            await sql.BeginAsync(SqlAccessMode.ReadWrite);
            await new LogicContext(sql).ExecuteAsyncForAffectedRows(new SqlStatement { Statement = "SET LOCAL lock_timeout = '10s';" });
            return sql;
        }

        // One block of a section written as the Reader, the job and Codex write it, inside the caller's open transaction.
        private static Task<string?[]> StoreAsync(SqlContext sql, long documentId, string path, int block, string text) =>
            LocalReadingPaths.StoreAsync(new LogicContext(sql), documentId, path, SectionHtml, "en", Digest, 2, new Dictionary<int, string> { [block] = text }, default);

        private static async Task<long> OpenBookAsync(Host host, string storage)
        {
            var (asset, _) = await ReadingTests.CreateBooksAsync(host, storage, "First Book", "Second Book");
            return await OpenAsync(host, asset);
        }

        private static async Task<long> OpenAsync(Host host, long asset)
        {
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + asset + "/open");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            return (await opened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!.DocumentId;
        }

        // Until another connection to the test database waits for a lock of the translation cache or of its progress counts.
        internal static async Task UntilWaitingAsync(Host host)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((long)(await host.ScalarAsync("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"))! == 0)
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "Nobody waits for a lock of the translation cache.");
                await Task.Delay(20);
            }
        }

        // Until the job's pass at that time is over, and the machine was checked by it.
        private static async Task CheckedAsync(LocalTranslationMachines machines, WorkerJobs jobs, Uri server, DateTimeOffset at)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (machines.CheckOf(server)?.CheckedAt != at || jobs.Describe("translationMachines", null) is not { State: not Jularr.Backend.Data.Enums.Worker.WorkerJobStateType.Running } pass
                || pass.LastFinishedAt != at.UtcDateTime)
            {
                var state = jobs.Describe("translationMachines", null);
                Assert.IsTrue(DateTime.UtcNow < deadline, "The machines were not checked at " + at + ": checked " + machines.CheckOf(server)?.CheckedAt + ", job " + state.State + " finished "
                    + state.LastFinishedAt + " next " + state.NextRunAt + " error " + state.LastErrorKey + ".");
                await Task.Delay(10);
            }
        }

        private static async Task<string> HtmlAsync(Host host, string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
            return html;
        }

        // The check the translationMachines job makes every thirty seconds, here at once.
        internal static async Task CheckMachinesAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/translation/machines/check");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static async Task<string?> AvailableAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/local/availability");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalTranslationAvailabilityV1>(JsonSerializerOptions.Web))!.Model;
        }

        private sealed class UnavailableMozilla : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public string Digest { get; set; } = new string('a', 64);
            public bool Unavailable { get; set; }
            public int GenerationCount { get; private set; }
            private int _requests;

            public string? FailingHost { get; set; }
            public int FailedGenerations { get; private set; }

            // Accepts the connection and never answers.
            public bool Silent { get; set; }
            public int Requests => _requests;

            // Started when a model download asks; the download then gets nothing until it is stopped.
            public TaskCompletionSource Pulling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                if (Silent) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (request.RequestUri!.AbsolutePath == "/api/pull")
                {
                    Pulling.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = Digest } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
                else if (request.RequestUri.AbsolutePath == "/api/generate" && request.RequestUri.Host == FailingHost)
                {
                    FailedGenerations++;
                    throw new HttpRequestException("The machine dropped out.");
                }
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual("translategemma:4b", input.RootElement.GetProperty("model").GetString());
                    GenerationCount++;
                    body = """{"response":"Kapitel Eins","eval_count":2,"eval_duration":200000000}""";
                }
                else throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
