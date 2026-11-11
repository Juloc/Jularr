using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The translation page as a column of cards: the job's status, every Ollama machine with its state, what it answers with and what failed last, the models.
    [TestClass]
    public sealed class TranslationPageTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Machines_ShowStateAnswerAndLastError_AndThePageFitsEveryWidth()
        {
            await using var ollama = ReaderTranslationTests.FakeOllama.Start();
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = ollama.Address.ToString() });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync($$"""
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'Translation';
                    UPDATE "InstanceSettings" SET "TranslationMachines" = ARRAY['http://127.0.0.1:9/', '{{ollama.Address}}'] WHERE "Id" = 1;
                    """);
                await ReaderTranslationTests.CheckMachinesAsync(page);

                foreach (var (width, height) in new[] { (1440, 900), (768, 1024), (375, 812) })
                {
                    await page.SetViewportSizeAsync(width, height);

                    // Progress: the status card says the job runs (the second machine answers) and the figures follow in their own cards.
                    await page.GotoAsync("/Admin/Instance/Translation");
                    await Assertions.Expect(page.Locator("[data-translation-prepare=ready]")).ToContainTextAsync("Running");
                    await Evidence.SaveAsync(page, $"translation-progress-{width}");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{width}: the progress tab scrolls sideways.");

                    // Setup: the first machine does not answer, the second translates; each card names its state and its last error.
                    await page.GotoAsync("/Admin/Instance/Translation?tab=setup");
                    var machines = page.Locator("[data-translation-machine]");
                    await Assertions.Expect(machines).ToHaveCountAsync(2);
                    await Assertions.Expect(machines.Nth(0)).ToContainTextAsync("Not reachable");
                    await Assertions.Expect(machines.Nth(0).Locator(".translation-facts__fact", new() { HasText = "Last error" })).ToContainTextAsync("Not reachable");
                    await Assertions.Expect(machines.Nth(1)).ToContainTextAsync("translating");
                    await Assertions.Expect(machines.Nth(1)).ToContainTextAsync("translategemma:4b");
                    await Assertions.Expect(machines.Nth(1).Locator(".translation-facts__fact", new() { HasText = "Last error" })).ToContainTextAsync("none since the start");
                    await Assertions.Expect(page.Locator(".translation-model", new() { HasText = "translategemma:4b" })).ToContainTextAsync("Installed");
                    await Evidence.SaveAsync(page, $"translation-setup-{width}");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{width}: the setup tab scrolls sideways.");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-translation-page");
                throw;
            }
        }

        // A book waiting to be translated: the languages and the books show as cards with their bars.
        [TestMethod]
        [Timeout(300_000)]
        public async Task Progress_ShowsLanguagesAndBooks_AsCardsWithBars()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "translation-progress.epub"), "Progress Book", "Local fixture", paragraphsPerChapter: 3);
            await using var ollama = ReaderTranslationTests.FakeOllama.Start(echo: true);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Storage:LocalPaths:0"] = folders.Library,
                ["LocalTranslation:Endpoint"] = ollama.Address.ToString(),
                ["HTTPS_PROXY"] = "http://127.0.0.1:9"
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "Progress Book");
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
                await ReaderTranslationTests.CheckMachinesAsync(page);
                await page.GotoAsync("/Read/1");
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
                await jularr.RunSqlAsync("""
                    INSERT INTO "ReadingPreferences" ("ProfileId", "WorkId", "Language")
                    SELECT profile."Id", asset."WorkId", 'de' FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" = 1
                    ON CONFLICT ("ProfileId", "WorkId") DO UPDATE SET "Language" = 'de'
                    """);

                foreach (var (width, height) in new[] { (1440, 900), (768, 1024), (375, 812) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync("/Admin/Instance/Translation");
                    var progress = page.Locator("[data-translation-progress]");
                    await Assertions.Expect(progress.Locator(".ui-section")).ToHaveCountAsync(3);
                    await Assertions.Expect(progress).ToContainTextAsync("Progress Book");
                    await Assertions.Expect(progress.Locator(".translation-progress__row progress").First).ToBeVisibleAsync();
                    await Evidence.SaveAsync(page, $"translation-progress-data-{width}");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{width}: the progress tab scrolls sideways.");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-translation-progress");
                throw;
            }
        }
    }
}
