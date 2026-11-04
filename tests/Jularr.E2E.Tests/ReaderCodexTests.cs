using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Owner re-translates a chapter and the book with Codex from the Reader's one re-translation while Ollama stays the default, and reads with Codex while no
    // Ollama answers; the AI page then lists the requests and adds up each chapter.
    [TestClass]
    public sealed class ReaderCodexTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Owner_RetranslatesAChapterWithCodex_AndTheReaderAndTheAiPageShowIt()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stand-in CLI is a shell script.");
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "codex-reader.epub"), "Codex Reader", "Local fixture", paragraphsPerChapter: 8);
            var codexFolder = Path.Combine(Path.GetTempPath(), "jularr-codex-reader-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(codexFolder, "home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth"), "");
            var executable = Path.Combine(codexFolder, "codex");
            File.WriteAllText(executable, AiPageTests.Cli.Replace("\r\n", "\n"));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                await using var ollama = ReaderTranslationTests.FakeOllama.Start();
                await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
                {
                    ["Storage:LocalPaths:0"] = folders.Library,
                    ["LocalTranslation:Endpoint"] = ollama.Address.ToString(),
                    ["Ai:CodexPath"] = executable,
                    ["Ai:CodexHome"] = home,
                    ["HTTPS_PROXY"] = "http://127.0.0.1:9"
                });
                await using var session = await BrowserSession.StartAsync(jularr);
                var page = session.Page;
                await ReadingPathTests.AddBookAsync(page, folders, "Codex Reader");
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

                // The Owner reads the chapter in German (Ollama translates), and the language panel offers Codex.
                await page.GotoAsync("/Read/1");
                await page.Locator("[data-action=language]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-language-choice=de]")).ToBeVisibleAsync();
                await page.Locator("[data-language-choice=de]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
                // The Reader's one re-translation offers the account's Codex models beside Ollama; its paragraph, chapter and book stay the same three choices.
                var area = page.Locator("[data-role=retranslate]");
                var status = area.Locator("[data-role=retranslate-status]");
                await Assertions.Expect(area).ToBeVisibleAsync();
                await Assertions.Expect(area.Locator("[data-retranslate-model]")).ToHaveCountAsync(3);
                await area.Locator("[data-retranslate-model='codex:gpt-one']").ClickAsync();
                await Assertions.Expect(area.Locator("[data-retranslate-model='codex:gpt-one']")).ToHaveAttributeAsync("aria-pressed", "true");
                await Evidence.SaveAsync(page, "reader-retranslate-codex-menu-desktop");

                // This chapter with Codex: one request for the passage, then the page shows Codex's text.
                await area.Locator("[data-retranslate=section]").ClickAsync();
                await Assertions.Expect(status).ToContainTextAsync("Done:", new() { Timeout = 60_000 });
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one:", new() { Timeout = 60_000 });
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
                await Assertions.Expect(page.Locator("[data-role=content] [data-translation-model='codex:gpt-one']").First).ToBeVisibleAsync();
                Assert.AreEqual("codex:gpt-one", await jularr.ScalarAsync("SELECT DISTINCT \"Model\" FROM \"ReadingTranslationChoices\" LIMIT 1") as string);

                // On a phone the same panel holds both models; then the whole book: every chapter in turn, both recorded for every reader.
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForTimeoutAsync(350);
                var tool = page.Locator(".reader__top [data-action=language]");
                if (!await tool.IsVisibleAsync())
                {
                    await page.Locator("[data-action=more]").ClickAsync();
                    tool = page.Locator("[data-panel=more] [data-overflow-for=language]");
                }
                await tool.ClickAsync();
                await Assertions.Expect(area).ToBeVisibleAsync();
                await area.Locator("[data-retranslate-model='codex:gpt-one']").ClickAsync();
                await Evidence.SaveAsync(page, "reader-retranslate-codex-menu-phone");
                await area.Locator("[data-retranslate=book]").ClickAsync();
                await Assertions.Expect(status).ToContainTextAsync("Done:", new() { Timeout = 90_000 });
                Assert.AreEqual(2L, await jularr.ScalarAsync("SELECT count(DISTINCT \"SectionIndex\") FROM \"ReadingTranslationChoices\""));
                await page.SetViewportSizeAsync(1280, 720);

                // The AI page lists what Codex did, with its cost.
                await page.GotoAsync("/Admin/Ai");
                var activity = page.Locator("#codex-activity");
                await Assertions.Expect(activity).ToContainTextAsync("translate");
                await Assertions.Expect(activity).ToContainTextAsync("gpt-one");
                await Assertions.Expect(activity).ToContainTextAsync("Done");
                await Assertions.Expect(page.Locator("#codex-usage")).ToContainTextAsync("Tokens today");
                await activity.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "ai-activity-desktop.png") });
                await page.SetViewportSizeAsync(390, 844);
                await page.ReloadAsync();
                await activity.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "ai-activity-phone.png") });
            }
            finally
            {
                Directory.Delete(codexFolder, true);
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Owner_WithoutAnAnsweringOllama_ReadsWithCodex_ComparesAnotherModel_AndTheAiPageAddsUpEachChapter()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stand-in CLI is a shell script.");
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "codex-alone.epub"), "Codex Alone", "Local fixture", paragraphsPerChapter: 8);
            var codexFolder = Path.Combine(Path.GetTempPath(), "jularr-codex-alone-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(codexFolder, "home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth"), "");
            var executable = Path.Combine(codexFolder, "codex");
            File.WriteAllText(executable, AiPageTests.Cli.Replace("\r\n", "\n"));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                // Ollama is set up but nothing answers on its address.
                await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
                {
                    ["Storage:LocalPaths:0"] = folders.Library,
                    ["LocalTranslation:Endpoint"] = "http://127.0.0.1:9/",
                    ["Ai:CodexPath"] = executable,
                    ["Ai:CodexHome"] = home,
                    ["HTTPS_PROXY"] = "http://127.0.0.1:9"
                });
                await using var session = await BrowserSession.StartAsync(jularr);
                var page = session.Page;
                await ReadingPathTests.AddBookAsync(page, folders, "Codex Alone");
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
                await jularr.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"CodexEffort\" = 'low' WHERE \"Id\" = 1");

                // The Owner's language panel offers the profile's languages, and Codex translates the chapter in whole passages.
                await page.GotoAsync("/Read/1");
                await page.Locator("[data-action=language]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-language-choice=de]")).ToBeVisibleAsync();
                Assert.AreEqual("codex:gpt-one", await page.Locator(".reader").GetAttributeAsync("data-local-model"));
                await page.Locator("[data-language-choice=de]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one:", new() { Timeout = 60_000 });
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\""));
                await Assertions.Expect(page.Locator("[data-translation-provider]")).ToHaveTextAsync("Engine: ChatGPT account · codex · gpt-one");

                // The other model of the account re-translates the same chapter for comparison.
                var area = page.Locator("[data-role=retranslate]");
                await Assertions.Expect(area).ToBeVisibleAsync();
                await Assertions.Expect(area.Locator("[data-retranslate-model]")).ToHaveCountAsync(2);
                await area.Locator("[data-retranslate-model='codex:gpt-two']").ClickAsync();
                await Evidence.SaveAsync(page, "reader-language-codex-alone-desktop");
                await area.Locator("[data-retranslate=section]").ClickAsync();
                await Assertions.Expect(area.Locator("[data-role=retranslate-status]")).ToContainTextAsync("Done:", new() { Timeout = 60_000 });
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-two:", new() { Timeout = 60_000 });

                // On a phone the same panel.
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
                var tool = page.Locator(".reader__top [data-action=language]");
                if (!await tool.IsVisibleAsync())
                {
                    await page.Locator("[data-action=more]").ClickAsync();
                    tool = page.Locator("[data-panel=more] [data-overflow-for=language]");
                }
                await tool.ClickAsync();
                await Assertions.Expect(area).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-language-codex-alone-phone");
                await page.SetViewportSizeAsync(1280, 720);

                // The AI page adds each chapter's requests up, with model and effort: one row per model on the same chapter.
                await page.GotoAsync("/Admin/Ai");
                var chapters = page.Locator("#codex-activity [data-codex-chapter]");
                await Assertions.Expect(chapters).ToHaveCountAsync(2);
                await Assertions.Expect(chapters.Nth(0)).ToContainTextAsync("gpt-two · Low");
                await Assertions.Expect(chapters.Nth(1)).ToContainTextAsync("gpt-one · Low");
                await Assertions.Expect(chapters.Nth(1)).ToContainTextAsync("Chapter 1");
                await Evidence.SaveAsync(page, "ai-activity-chapters-desktop");
                await page.SetViewportSizeAsync(390, 844);
                await page.ReloadAsync();
                await Evidence.SaveAsync(page, "ai-activity-chapters-phone");
            }
            finally
            {
                Directory.Delete(codexFolder, true);
            }
        }
    }
}
