using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The library is translated ahead of reading into the languages profiles use, books being read first, into the cache the Reader reads.
    [TestClass]
    public sealed class BookTranslationTests
    {
        [TestMethod]
        public async Task Job_TranslatesTheBookBeingReadFirst_IntoTheProfilesLanguages_AndTheReaderGetsItAtOnce()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");

            // Off, there is no translating at all.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await Assert.ThrowsExactlyAsync<System.Security.SecurityException>(() => Translate(scope));
            }
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            // Every profile reads English, the books' own language: nothing to do.
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'en%' ORDER BY \"Locale\" LIMIT 1)");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsFalse(await Translate(scope));
            }
            Assert.AreEqual(0, ollama.GenerationCount);

            // A profile in German, reading the second book: that book comes first, then the first one.
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "Locator", "Progression", "IsCompleted", "UpdatedAt")
                SELECT profile."Id", asset."WorkId", 2, 's0/b0', 0.1, FALSE, now()
                FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" =
                """ + second);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            Assert.IsTrue(ollama.GenerationCount > 0);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + secondDocument.DocumentId));

            // Round after round until the library is done; then there is nothing left, and nothing is translated twice.
            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            }
            var generations = ollama.GenerationCount;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsFalse(await Translate(scope));
            }
            Assert.AreEqual(generations, ollama.GenerationCount);
            foreach (var document in new[] { firstDocument, secondDocument })
            {
                var sections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + document.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(sections, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + document.DocumentId));
            }

            // The Reader finds the chapter translated without asking the model.
            using var translated = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + firstDocument.DocumentId + "/sections/0/local-translations/de/translategemma:4b?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, translated.StatusCode, await translated.Content.ReadAsStringAsync());
            var blocks = JsonSerializer.Deserialize<string[]>((await translated.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks)!;
            Assert.IsTrue(blocks.Length > 0 && blocks.Any(block => block.StartsWith("DE ", StringComparison.Ordinal)), string.Join(" | ", blocks));

            // A language chosen for a book is a target too; a book in that language is left alone.
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'fr' WHERE \"Id\" = " + secondDocument.DocumentId);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ReadingPreferences" ("ProfileId", "WorkId", "Language")
                SELECT profile."Id", asset."WorkId", 'fr' FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" =
                """ + first);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 400, "The job never finishes.");
            }
            Assert.IsTrue(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/fr/%' AND \"ReadingDocumentId\" = " + firstDocument.DocumentId) is > 0L);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/fr/%' AND \"ReadingDocumentId\" = " + secondDocument.DocumentId));
        }

        [TestMethod]
        public async Task Job_BooksNobodyReads_AreTranslatedOneAfterTheOther()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // The book with the lower id is finished before a single section of the other one is translated.
            var (early, late) = firstDocument.DocumentId < secondDocument.DocumentId ? (firstDocument, secondDocument) : (secondDocument, firstDocument);
            var earlySections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + early.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope))
            {
                Assert.IsTrue(++rounds < 200, "The job never finishes.");
                var lateStarted = Convert.ToInt64(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%' AND \"ReadingDocumentId\" = " + late.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                if (lateStarted == 0) continue;
                var earlyDone = Convert.ToInt64(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%' AND \"ReadingDocumentId\" = " + early.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(earlySections, earlyDone, "The second book started before the first one was done.");
            }
        }

        [TestMethod]
        public async Task Job_ASectionWithoutText_IsDoneOnceAndTheRoundsMoveOn()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // The first section becomes a page of one picture: no text block at all.
            var id = document.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingResources\" SET \"Bytes\" = convert_to('<p><img src=\"a.png\"/></p>', 'UTF8') WHERE \"ReadingDocumentId\" = " + id + " AND \"Path\" = ':section/00000'");
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Structure\" = jsonb_set(\"Structure\", '{Sections,0,Blocks}', '0') WHERE \"Id\" = " + id);

            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job keeps picking the section without text.");
            }
            var sections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + document.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(sections, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + document.DocumentId));
        }

        [TestMethod]
        public async Task Progress_CountsFinishedSectionsPerLanguageAndBook_AndNamesTheSectionInWork()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // Before the first round: every section of both books waits, nothing is in work, nothing can be estimated.
            var before = await ReadProgressAsync(host);
            Assert.AreEqual(1, before.Languages.Length);
            Assert.AreEqual("de", before.Languages[0].Language);
            Assert.AreEqual(0, before.Languages[0].Done);
            Assert.IsTrue(before.Languages[0].Total > 0);
            Assert.AreEqual(2, before.Books.Length);
            Assert.IsNull(before.Current);
            Assert.IsNull(before.SecondsRemaining);

            // The time left is the blocks still to do over the blocks per minute, not the sections: with 2 blocks a minute it is half the blocks in minutes.
            var tracker = host.Services.GetRequiredService<Jularr.Backend.Service.Core.BookTranslationProgress>();
            for (var block = 0; block < 20; block++) tracker.BlockDone();
            var totalBlocks = Convert.ToInt64(await host.ScalarAsync("SELECT sum((section.value ->> 'Blocks')::integer) FROM \"ReadingDocuments\" AS document, jsonb_array_elements(document.\"Structure\" -> 'Sections') AS section (value)"), System.Globalization.CultureInfo.InvariantCulture);
            var estimated = await ReadProgressAsync(host);
            Assert.AreEqual(2.0, estimated.BlocksPerMinute);
            Assert.AreEqual((long)Math.Round(totalBlocks / 2.0 * 60), estimated.SecondsRemaining);

            // One round: the section it picked is named.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            var during = await ReadProgressAsync(host);
            Assert.IsNotNull(during.Current);
            Assert.IsTrue(during.Current.Title is "First Book" or "Second Book");
            Assert.IsTrue(during.Current.Section >= 1);
            Assert.AreEqual("de", during.Current.Language);

            // A book that is one volume of a series is listed with its number.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkUnits\" (\"WorkId\", \"WorkUnitTypeId\", \"Number\") SELECT asset.\"WorkId\", 3, 14 FROM \"MediaAssets\" AS asset WHERE asset.\"Id\" = " + first);
            await host.Fixture.RunSqlAsync("UPDATE \"MediaAssets\" SET \"WorkUnitId\" = (SELECT unit.\"Id\" FROM \"WorkUnits\" AS unit WHERE unit.\"WorkId\" = \"MediaAssets\".\"WorkId\" AND unit.\"Number\" = 14) WHERE \"Id\" = " + first);
            var labelled = await ReadProgressAsync(host);
            Assert.AreEqual("14", labelled.Books.Single(book => book.Title == "First Book").Volume);
            Assert.IsNull(labelled.Books.Single(book => book.Title == "Second Book").Volume);

            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            }
            var done = await ReadProgressAsync(host);
            Assert.AreEqual(done.Languages[0].Total, done.Languages[0].Done);
            Assert.IsTrue(done.Books.All(book => book.Done == book.Total && book.Total > 0));
            Assert.IsNull(done.Current);
            Assert.IsNull(done.SecondsRemaining);
            Assert.IsNull(done.IssueKey);
            Assert.IsTrue(firstDocument.DocumentId > 0);

            // The page shows the part, and reading only that part answers the fragment the page swaps in; the Jobs page loads.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-translation-progress");
            StringAssert.Contains(html, "First Book");
            using var fragment = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?handler=Progress");
            var part = await fragment.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, fragment.StatusCode, part);
            Assert.IsTrue(part.TrimStart().StartsWith("<div", StringComparison.Ordinal) && part.Contains("data-translation-progress", StringComparison.Ordinal), part);
            // The Jobs page still loads with the translation module on (its drawer needs the job row, which a started worker creates).
            using var jobs = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            Assert.AreEqual(HttpStatusCode.OK, jobs.StatusCode, await jobs.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public void Progress_TracksTheSectionPaceAndWhyNothingHappens()
        {
            var clock = new Clock();
            var progress = new Jularr.Backend.Service.Core.BookTranslationProgress(clock);
            Assert.IsNull(progress.Snapshot().Current);

            progress.Picked(7, 3, "de");
            clock.Now += TimeSpan.FromSeconds(90);
            var waiting = progress.Snapshot();
            Assert.AreEqual(3, waiting.Current!.Section);
            Assert.AreEqual(90, waiting.SecondsWithoutProgress);

            // A block resets the time without progress; blocks count towards the pace of the last ten minutes.
            progress.BlockDone();
            for (var block = 0; block < 9; block++) progress.BlockDone();
            var moving = progress.Snapshot();
            Assert.AreEqual(0, moving.SecondsWithoutProgress);
            Assert.AreEqual(1.0, moving.BlocksPerMinute);

            clock.Now += TimeSpan.FromMinutes(11);
            Assert.AreEqual(0.0, progress.Snapshot().BlocksPerMinute);

            progress.Idle("noMachine");
            var idle = progress.Snapshot();
            Assert.IsNull(idle.Current);
            Assert.AreEqual("noMachine", idle.IssueKey);
        }

        private static async Task<BookTranslationProgressV1> ReadProgressAsync(AuthenticationTests.Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/progress");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BookTranslationProgressV1>(JsonSerializerOptions.Web))!;
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static async Task<bool> Translate(AsyncServiceScope scope) =>
            (await scope.ServiceProvider.GetRequiredService<TranslateReadingBooksV1>().ExecuteAsync<UpdateResult>(new BookTranslationParametersV1(), default, CancellationToken.None)).Success;

        private sealed class FakeOllama : HttpMessageHandler
        {
            public int GenerationCount { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = new string('a', 64) } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var prompt = input.RootElement.GetProperty("prompt").GetString()!;
                    GenerationCount++;
                    var language = prompt.Contains("into French", StringComparison.Ordinal) ? "FR" : "DE";
                    body = JsonSerializer.Serialize(new { response = language + " " + prompt[(prompt.LastIndexOf('\n') + 1)..], eval_count = 2, eval_duration = 200000000 });
                }
                else throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
