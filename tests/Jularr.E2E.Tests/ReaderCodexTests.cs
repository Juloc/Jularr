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
                await ReaderTranslationTests.CheckMachinesAsync(page);

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

        // The stand-in CLI of the AI page, slowed (a second a request, a hundredth of a second a line of the streamed answer), noting every request in `asked`, failing from the request
        // after the number in `failafter` and hitting its usage limit while `limit` exists.
        private static string SlowCli => AiPageTests.Cli.Replace("\r\n", "\n").Replace("prompt=$(printf '%b' \"$text\")\n", "prompt=$(printf '%b' \"$text\")\n" + Hook, StringComparison.Ordinal)
            .Replace("printf '{\"method\":\"item/agentMessage/delta\"", "sleep 0.01; printf '{\"method\":\"item/agentMessage/delta\"", StringComparison.Ordinal);

        private const string Hook = """
                                  echo asked >> "$CODEX_HOME/asked"
                                  sleep 1
                                  if [ -f "$CODEX_HOME/failafter" ] && [ "$(wc -l < "$CODEX_HOME/asked")" -gt "$(cat "$CODEX_HOME/failafter")" ]; then
                                    printf '{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"The model failed."}}}}\n'
                                    continue
                                  fi
                                  if [ -f "$CODEX_HOME/limit" ]; then
                                    printf '{"method":"error","params":{"threadId":"thread-1","turnId":"turn-1","willRetry":false,"error":{"message":"You hit your usage limit. Upgrade to Pro, or try again at Nov 4th, 2099 7:41 AM."}}}\n'
                                    printf '{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"You hit your usage limit."}}}}\n'
                                    continue
                                  fi

                """ + "\n";

        private static string Setup(string folder, out string home)
        {
            home = Path.Combine(folder, "home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth"), "");
            var executable = Path.Combine(folder, "codex");
            File.WriteAllText(executable, SlowCli);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return executable;
        }

        [TestMethod]
        [Timeout(420_000)]
        public async Task Owner_WithoutAnAnsweringOllama_TranslatesAChapterByButtonOnly_LiveInChunks_AndKeepsItWhenReopened()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stand-in CLI is a shell script.");
            Assert.IsTrue(SlowCli.Contains("sleep 1", StringComparison.Ordinal) && SlowCli.Contains("sleep 0.01", StringComparison.Ordinal), "The stand-in CLI must be slowed.");
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "codex-alone.epub"), "Codex Alone", "Local fixture", paragraphsPerChapter: 320);
            var codexFolder = Path.Combine(Path.GetTempPath(), "jularr-codex-alone-" + Guid.NewGuid().ToString("N"));
            var executable = Setup(codexFolder, out var home);
            var asked = () => File.Exists(Path.Combine(home, "asked")) ? File.ReadAllLines(Path.Combine(home, "asked")).Length : 0;
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
                await ReaderTranslationTests.CheckMachinesAsync(page);
                await jularr.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"CodexEffort\" = 'low' WHERE \"Id\" = 1");

                // Choosing German shows the original text and the button; Codex is asked for nothing.
                await page.GotoAsync("/Read/1");
                await page.Locator("[data-action=language]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-language-choice=de]")).ToBeVisibleAsync();
                await page.Locator("[data-language-choice=de]").ClickAsync();
                var bar = page.Locator("[data-role=codex]");
                await Assertions.Expect(bar.Locator("[data-codex=start]")).ToBeVisibleAsync(new() { Timeout = 30_000 });
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("not translated yet");
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Paragraph 1 of a long chapter");
                await page.WaitForTimeoutAsync(1500);
                Assert.AreEqual(0, asked(), "Codex must not be asked until the button is pressed.");
                Assert.IsFalse((await page.Locator("[data-role=content]").InnerTextAsync()).Contains("Codex gpt-one:", StringComparison.Ordinal), "The original text shows until the button is pressed.");
                await Assertions.Expect(page.Locator("[data-translation-provider]")).ToHaveTextAsync("Engine: ChatGPT account · codex · gpt-one");
                await Assertions.Expect(page.Locator("[data-role=translation-status]")).ToBeHiddenAsync();
                await Evidence.SaveAsync(page, "reader-codex-button-desktop");

                // The press translates the chapter in several requests; the first answer shows while the next ones run, with the time left.
                await bar.Locator("[data-codex=start]").ClickAsync();
                await Assertions.Expect(bar.Locator("[data-codex=stop]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one:", new() { Timeout = 60_000 });
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("Translating the chapter with Codex");
                await Assertions.Expect(bar).ToHaveAttributeAsync("data-busy", "true");
                await Assertions.Expect(bar.Locator(".reader__codex-icon--accent")).ToBeVisibleAsync();
                Assert.IsTrue(asked() >= 1);
                await Evidence.SaveAsync(page, "reader-codex-live-desktop");
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("This chapter is translated.", new() { Timeout = 120_000 });
                Assert.IsTrue(asked() >= 3, "A chapter of 320 blocks needs several requests.");
                await Assertions.Expect(bar.Locator("[data-codex=next]")).ToBeVisibleAsync();
                await Assertions.Expect(bar.Locator("[data-codex=start]")).ToBeHiddenAsync();
                await Assertions.Expect(bar).ToHaveAttributeAsync("data-tone", "success");
                await Assertions.Expect(bar.Locator(".reader__codex-icon--success")).ToBeVisibleAsync();
                await Assertions.Expect(bar.Locator(".reader__codex-icon--accent")).ToBeHiddenAsync();
                var text = await page.Locator("[data-role=content]").InnerTextAsync();
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, "(^|\n)Paragraph \\d+"), "Every paragraph is translated: " + text);
                Assert.IsTrue(text.Contains("Codex gpt-one: Paragraph 320", StringComparison.Ordinal), text);
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\""));
                await Evidence.SaveAsync(page, "reader-codex-done-desktop");

                // Reopened, the chapter is translated without a press and without a request.
                var before = asked();
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one: Paragraph 320", new() { Timeout = 60_000 });
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("This chapter is translated.");
                await page.WaitForTimeoutAsync(1500);
                Assert.AreEqual(before, asked());

                // The next chapter only on its own button.
                await bar.Locator("[data-codex=next]").ClickAsync();
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("next chapter", new() { Timeout = 30_000 });
                await Assertions.Expect(bar.Locator("[data-role=codex-text]")).ToContainTextAsync("The next chapter is translated.", new() { Timeout = 120_000 });
                Assert.IsTrue(asked() > before);
                Assert.AreEqual(2L, await jularr.ScalarAsync("SELECT count(DISTINCT regexp_replace(\"Path\", '.*/', '')) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/codex:%'"));

                // On a tablet and a phone the bar holds its text and buttons.
                foreach (var (width, height, name) in new[] { (768, 1024, "tablet"), (375, 812, "phone") })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.WaitForTimeoutAsync(400);
                    await Assertions.Expect(bar).ToBeVisibleAsync();
                    await Evidence.SaveAsync(page, "reader-codex-done-" + name);
                }
            }
            finally
            {
                Directory.Delete(codexFolder, true);
            }
        }

        [TestMethod]
        [Timeout(420_000)]
        public async Task Owner_WhenCodexFailsOrIsLimited_IsToldWhy_AndTheRetryKeepsTheFinishedPart()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stand-in CLI is a shell script.");
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "codex-fails.epub"), "Codex Fails", "Local fixture", paragraphsPerChapter: 320);
            var codexFolder = Path.Combine(Path.GetTempPath(), "jularr-codex-fails-" + Guid.NewGuid().ToString("N"));
            var executable = Setup(codexFolder, out var home);
            try
            {
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
                await ReadingPathTests.AddBookAsync(page, folders, "Codex Fails");
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
                await ReaderTranslationTests.CheckMachinesAsync(page);
                await page.GotoAsync("/Read/1");
                await page.Locator("[data-action=language]").ClickAsync();
                await page.Locator("[data-language-choice=de]").ClickAsync();
                var bar = page.Locator("[data-role=codex]");
                var line = bar.Locator("[data-role=codex-text]");
                await Assertions.Expect(bar.Locator("[data-codex=start]")).ToBeVisibleAsync(new() { Timeout = 30_000 });

                // The first request answers, then Codex fails: the finished part stays, the cause is named, and the text never says "unavailable".
                File.WriteAllText(Path.Combine(home, "failafter"), "1");
                await bar.Locator("[data-codex=start]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one:", new() { Timeout = 60_000 });
                await Assertions.Expect(line).ToContainTextAsync("Codex could not translate a part of the chapter", new() { Timeout = 60_000 });
                await Assertions.Expect(bar.Locator("[data-codex=retry]")).ToBeVisibleAsync();
                await Assertions.Expect(line).Not.ToContainTextAsync("unavailable");
                await Assertions.Expect(bar).ToHaveAttributeAsync("data-tone", "danger");
                await Assertions.Expect(bar.Locator(".reader__codex-icon--danger")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Codex gpt-one:");
                var finished = await page.Locator("[data-role=content]").InnerTextAsync();
                await Evidence.SaveAsync(page, "reader-codex-error-desktop");

                // The retry after the cause is gone asks only for the rest and finishes; the first part is not translated again.
                File.Delete(Path.Combine(home, "failafter"));
                var asked = File.ReadAllLines(Path.Combine(home, "asked")).Length;
                await bar.Locator("[data-codex=retry]").ClickAsync();
                await Assertions.Expect(line).ToContainTextAsync("This chapter is translated.", new() { Timeout = 180_000 });
                Assert.IsTrue(File.ReadAllLines(Path.Combine(home, "asked")).Length - asked <= 3, "Only the missing part is asked for again.");
                Assert.IsTrue((await page.Locator("[data-role=content]").InnerTextAsync()).Contains(finished[..40], StringComparison.Ordinal));

                // The usage limit of the account is named as such, on the next chapter.
                File.WriteAllText(Path.Combine(home, "limit"), "");
                await bar.Locator("[data-codex=next]").ClickAsync();
                await Assertions.Expect(line).ToContainTextAsync("usage limit", new() { Timeout = 60_000 });
                await Assertions.Expect(bar).ToHaveAttributeAsync("data-tone", "warning");
                await Assertions.Expect(bar.Locator("[data-codex=retry]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-codex-limit-desktop");
                await page.SetViewportSizeAsync(375, 812);
                await page.WaitForTimeoutAsync(400);
                await Assertions.Expect(bar).ToBeVisibleAsync();
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "The Codex bar makes the page wider than the phone.");
                await Evidence.SaveAsync(page, "reader-codex-limit-phone");
            }
            finally
            {
                Directory.Delete(codexFolder, true);
            }
        }
    }
}
