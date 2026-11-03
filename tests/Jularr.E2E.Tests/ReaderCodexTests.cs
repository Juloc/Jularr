using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Owner re-translates a chapter and the book with Codex from the Reader's one re-translation while Ollama stays the default; the AI page then lists the requests.
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
                // The Reader's one re-translation offers Codex beside Ollama; its paragraph, chapter and book stay the same three choices.
                var area = page.Locator("[data-role=retranslate]");
                var status = area.Locator("[data-role=retranslate-status]");
                await Assertions.Expect(area).ToBeVisibleAsync();
                await Assertions.Expect(area.Locator("[data-retranslate-model]")).ToHaveCountAsync(2);
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
    }
}
