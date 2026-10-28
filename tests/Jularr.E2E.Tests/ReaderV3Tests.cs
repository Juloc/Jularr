using System.IO.Compression;
using System.Security;
using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Reader's header, bottom bar, pages, settings and read-aloud as the owner's third Reader mockup describes them.
    [TestClass]
    public sealed class ReaderV3Tests
    {
        private static readonly string[] Sentences =
        [
            "The archive fell silent after sunset, and even the old clocks seemed to whisper.",
            "Mira had worked there for seven years, yet she had never opened the locked room at the end of the gallery.",
            "Extraordinarily unconventional librarians remember incomprehensible catalogue numbers.",
            "A pale light shone beneath its door, as though somebody had left a lantern burning on the other side.",
            "She set down her ledger and followed the light down the narrow staircase."
        ];

        // The tools of the header, the longest-staying first.
        private static readonly string[] ToolOrder = ["language", "text", "mode", "speech", "bookmark", "search", "notes", "paper", "select", "chapters"];

        [TestMethod]
        [Timeout(300_000)]
        public async Task Header_OverflowsByWidth_LanguageButtonShowsItsHint_SettingsOpenWithOnePress()
        {
            using var folders = new Folders("Books");
            WriteReaderEpub(Path.Combine(folders.Library, "Books", "reader-v3.epub"), "The Midnight Archive", "Local fixture", 6, 36);

            // No model list is reachable, so no reading language is configured.
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library, ["HTTPS_PROXY"] = "http://127.0.0.1:9" });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                await OpenReaderAsync(page, 1440, 900);

                // Without the Translation module there is no language button.
                Assert.AreEqual(0, await page.Locator("[data-overflow=language]").CountAsync());
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
                await OpenReaderAsync(page, 1440, 900);
                var language = page.Locator(".reader__tools [data-overflow=language]");
                await Assertions.Expect(language).ToBeVisibleAsync();
                await language.ClickAsync();
                var hint = page.Locator("[data-panel=language]");
                await Assertions.Expect(hint).ToBeVisibleAsync();
                await Assertions.Expect(hint).ToContainTextAsync("No translation engine is available right now.");
                Assert.AreEqual(0, await hint.GetByRole(AriaRole.Link).CountAsync(), "An unavailable engine is not a missing profile language.");
                await Evidence.SaveAsync(page, "reader-v3-language-hint-1440");
                await page.Keyboard.PressAsync("Escape");

                // Whatever does not fit leaves by rank, never beyond the bar, and its stand-in appears in More.
                var hiddenBefore = -1;
                foreach (var width in new[] { 1440, 1100, 900, 700, 520, 390, 340, 320 })
                {
                    await page.SetViewportSizeAsync(width, 844);
                    await page.WaitForTimeoutAsync(350);
                    var state = await BarsAsync(page);
                    var hidden = state.GetProperty("hidden").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    var footerHidden = state.GetProperty("footerHidden").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    var entries = state.GetProperty("entries").EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
                    var present = ToolOrder.Where(name => state.GetProperty("present").EnumerateArray().Any(item => item.GetString() == name)).ToArray();
                    Evidence.Log($"[reader-v3] {width}: hidden {string.Join(',', hidden)} footer {string.Join(',', footerHidden)} entries {string.Join(',', entries)}");
                    Assert.IsTrue(state.GetProperty("topFits").GetBoolean() && state.GetProperty("footerFits").GetBoolean(), $"{width}: the bars stay inside the window.");
                    CollectionAssert.AreEqual(present.Skip(present.Length - hidden.Length).ToArray(), ToolOrder.Where(hidden.Contains).ToArray(), $"{width}: the tools with the highest ranks leave first.");
                    CollectionAssert.AreEqual(hidden.Concat(footerHidden).Order().ToArray(), entries.Where(name => name != "chapters" || hidden.Contains("chapters")).ToArray(), $"{width}: More offers exactly what left.");
                    Assert.IsTrue(hidden.Length >= hiddenBefore, $"{width}: a narrower window never brings a tool back.");
                    hiddenBefore = hidden.Length;
                    if (width <= 520)
                    {
                        Assert.IsFalse(hidden.Contains("language"), $"{width}: the language button stays in the bar.");
                    }

                    // One press opens the settings, in the bar and in every width.
                    await page.Locator("[data-action=settings]").ClickAsync();
                    await Assertions.Expect(page.Locator("[data-panel=settings]")).ToBeVisibleAsync();
                    await Assertions.Expect(page.Locator("[data-reader-group=text]")).ToBeVisibleAsync();
                    await page.Keyboard.PressAsync("Escape");
                    await Assertions.Expect(page.Locator("[data-panel=settings]")).ToBeHiddenAsync();
                    if (width is 1440 or 1024 or 390 or 1100)
                    {
                        await Evidence.SaveAsync(page, "reader-v3-header-" + width + "-light");
                    }
                }

                // A tool that left works from the menu in one more press.
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForTimeoutAsync(350);
                await page.Locator("[data-action=more]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=more] [data-overflow-for=search]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-v3-more-390-light");
                await page.Locator("[data-panel=more] [data-overflow-for=search]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=search]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-panel=more]")).ToBeHiddenAsync();
                await page.Keyboard.PressAsync("Escape");
                await page.SetViewportSizeAsync(340, 844);
                await page.WaitForTimeoutAsync(350);
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-panel=more] [data-overflow-for=bookmark]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-reader-bookmark]").First).ToHaveAttributeAsync("aria-pressed", "true");

                // The same in the dark.
                await page.EvaluateAsync("() => localStorage.setItem('jularr-ui-mode', 'dark')");
                foreach (var width in new[] { 1440, 1024, 390 })
                {
                    await OpenReaderAsync(page, width, 844);
                    await Evidence.SaveAsync(page, "reader-v3-header-" + width + "-dark");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.Where(problem => !problem.Contains("proxy", StringComparison.OrdinalIgnoreCase)).ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-v3-header-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ModeButton_CyclesBookScrollAndFlatPages_AndTheDeviceKeepsIt()
        {
            using var folders = new Folders("Books");
            WriteReaderEpub(Path.Combine(folders.Library, "Books", "reader-v3.epub"), "The Midnight Archive", "Local fixture", 2, 36);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                await OpenReaderAsync(page, 1440, 900);
                var reader = page.Locator(".reader");
                var mode = page.Locator(".reader__top [data-reader-mode]");
                var paper = page.Locator(".reader__paper");
                await Assertions.Expect(mode).ToHaveAttributeAsync("aria-label", "Reading mode: Book");
                await Assertions.Expect(mode.Locator("[data-mode-icon=book]")).ToBeVisibleAsync();

                // Book → scroll: the text runs down, the book view is gone.
                await mode.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-mode", "scroll");
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-book-ready", "false");
                await Assertions.Expect(mode.Locator("[data-mode-icon=scroll]")).ToBeVisibleAsync();

                // Scroll → flat pages: paged text on the plain paper, turned at once without a book.
                await mode.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-mode", "paged");
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-paging", "flat");
                await Assertions.Expect(mode).ToHaveAttributeAsync("aria-label", "Reading mode: Flat pages");
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-book-ready", "false");
                await Assertions.Expect(paper).ToHaveCSSAsync("opacity", "1");
                await page.Locator("[data-turn='1']").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-page", "3");
                Assert.AreEqual(0, await page.Locator(".reader-book__turn").CountAsync(), "Flat pages turn without paper leaves.");
                await Evidence.SaveAsync(page, "reader-mode-flat");

                // The device keeps the mode; flat → book brings the book view back.
                await page.ReloadAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-paging", "flat");
                await Assertions.Expect(paper).ToHaveCSSAsync("opacity", "1");
                await mode.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-paging", "book");
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-book-ready", "true");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-mode-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ProgressSlider_ShowsChapterTicks_PreviewsAndSeeksTheWholeBook()
        {
            using var folders = new Folders("Books");
            WriteReaderEpub(Path.Combine(folders.Library, "Books", "reader-v3.epub"), "The Midnight Archive", "Local fixture", 6, 36);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                await OpenReaderAsync(page, 1440, 900);
                var seek = page.Locator("[data-role=seek] input[type=range]");
                var reader = page.Locator(".reader");
                await Assertions.Expect(seek).ToHaveAttributeAsync("min", "0");
                await Assertions.Expect(seek).ToHaveAttributeAsync("max", "1000");
                await Assertions.Expect(page.Locator("[data-role=ticks] .reader__tick")).ToHaveCountAsync(6);
                var ticks = await page.Locator("[data-role=ticks] .reader__tick").EvaluateAllAsync<double[]>("items => items.map(item => parseFloat(item.style.getPropertyValue('--tick')))");
                Assert.AreEqual(0, ticks[0]);
                Assert.IsTrue(ticks.Zip(ticks.Skip(1), (a, b) => b - a).All(step => step is > 10 and < 25), "Six chapters of about the same length begin about every sixth.");
                await Assertions.Expect(page.Locator("[data-role=position]")).ToHaveTextAsync("0 %");

                // The pages label counts the book's pages once they are counted; its numbers and the slider come from the one page model.
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookPageState === 'ready'", null, new() { Timeout = 60_000 });
                var label = await page.Locator("[data-role=pages]").InnerTextAsync();
                var total = int.Parse(await reader.GetAttributeAsync("data-book-page-count") ?? "0");
                StringAssert.Matches(label, new System.Text.RegularExpressions.Regex($"^Pages 1–2 of {total}$"));
                Assert.IsTrue(total > 12);

                // Dragging shows the chapter and the share at the thumb without moving the book.
                var url = page.Url;
                await seek.EvaluateAsync("input => { input.value = '760'; input.dispatchEvent(new Event('input', { bubbles: true })); }");
                await Assertions.Expect(page.Locator("[data-role=caption] .truncated__text")).ToHaveTextAsync("Chapter Five");
                await Assertions.Expect(page.Locator("[data-role=position]")).ToHaveTextAsync("76 %");
                Assert.AreEqual(url, page.Url);
                Assert.AreEqual("1", await reader.GetAttributeAsync("data-page"));
                await Evidence.SaveAsync(page, "reader-v3-slider-drag-1440");

                // Releasing it in another chapter opens that chapter there.
                await seek.EvaluateAsync("input => input.dispatchEvent(new Event('change', { bubbles: true }))");
                await page.WaitForURLAsync("**/Read/1?s=4&f=*");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.bookReady === 'true'", null, new() { Timeout = 30_000 });
                Assert.AreEqual("4", await reader.GetAttributeAsync("data-section"));
                await page.WaitForFunctionAsync("() => document.querySelector('[data-role=seek] input').valueAsNumber > 690", null, new() { Timeout = 10_000 });
                var landed = int.Parse(await seek.InputValueAsync());
                Assert.IsTrue(Math.Abs(landed - 760) <= 70, $"The slider shows where the place is: {landed}.");

                // Inside the chapter it turns to the page without leaving.
                var startPage = int.Parse((await reader.GetAttributeAsync("data-page"))!);
                url = page.Url;
                await ReadingPathTests.SeekAsync(page, 820);
                await page.WaitForFunctionAsync($"() => Number(document.querySelector('.reader').dataset.page) > {startPage}");
                Assert.AreEqual(url, page.Url);
                var after = int.Parse(await seek.InputValueAsync());
                Assert.IsTrue(Math.Abs(after - 820) <= 70, $"The slider settles on the page: {after}.");
                await Assertions.Expect(page.Locator("[data-role=position]")).ToHaveTextAsync($"{(int)Math.Round(after / 10.0, MidpointRounding.AwayFromZero)} %");

                // The keys reach both ends of the book.
                await seek.FocusAsync();
                await page.Keyboard.PressAsync("Home");
                await page.WaitForURLAsync("**/Read/1?s=0&f=*");
                await seek.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await page.WaitForURLAsync("**/Read/1?s=5&f=1.0000");
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookReady === 'true' && Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
                await Assertions.Expect(page.Locator("[data-role=position]")).ToHaveTextAsync("100 %");

                // The slider keeps its place across the widths.
                foreach (var width in new[] { 1024, 390 })
                {
                    await page.SetViewportSizeAsync(width, 844);
                    await page.WaitForTimeoutAsync(500);
                    var box = (await page.Locator(".reader__seek").BoundingBoxAsync())!;
                    var footer = (await page.Locator(".reader__footer").BoundingBoxAsync())!;
                    Assert.IsTrue(Math.Abs(box.Y + box.Height / 2 - footer.Y) <= 8, "The slider sits on the bar's upper edge.");
                    Assert.IsTrue(box.Width >= footer.Width - 2, "The slider spans the bar.");
                    await Evidence.SaveAsync(page, "reader-v3-bottom-" + width + "-light");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-v3-slider-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task DoublePage_TextBoxAndNumberFollowEachPaper_ProfileKeepsTheTextSettings()
        {
            using var folders = new Folders("Books");
            WriteReaderEpub(Path.Combine(folders.Library, "Books", "reader-v3.epub"), "The Midnight Archive", "Local fixture", 6, 36);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                var reader = page.Locator(".reader");
                foreach (var (width, height) in new[] { (1440, 900), (1280, 720), (1024, 768) })
                {
                    await OpenReaderAsync(page, width, height);
                    Assert.AreEqual("2", await reader.GetAttributeAsync("data-columns"));
                    await AssertPaperPositionsAsync(page, $"{width}x{height} first spread");

                    // One page model: the label, the page counter, a turn and the slider agree.
                    var count = int.Parse((await reader.GetAttributeAsync("data-page-count"))!);
                    await Assertions.Expect(page.Locator("[data-role=pages]")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex($@"^(?:Pages 1–2 of (?:{count}|\d+)|Ch\. 1–2/(?:{count}|\d+))$"));
                    var before = int.Parse(await page.Locator("[data-role=seek] input").InputValueAsync());
                    await page.Locator("[data-turn='1']").ClickAsync();
                    await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle' && document.querySelector('.reader').dataset.page === '3'");
                    await Assertions.Expect(page.Locator("[data-role=pages]")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^(?:Pages 3–4 of \d+|Ch\. 3–4/\d+)$"));
                    CollectionAssert.AreEqual(new[] { "3", "4" }, await page.Locator("[data-book-number]").EvaluateAllAsync<string[]>("items => items.map(item => item.textContent)"));
                    Assert.IsTrue(int.Parse(await page.Locator("[data-role=seek] input").InputValueAsync()) > before, "The slider follows the turned page.");
                    await AssertPaperPositionsAsync(page, $"{width}x{height} second spread");
                    await page.Locator("[data-turn='-1']").ClickAsync();
                    await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle' && document.querySelector('.reader').dataset.page === '1'");
                    await Evidence.SaveAsync(page, $"reader-v3-double-{width}-light");
                }

                // Mirroring the book must not move its page numbers off the physical paper's centre.
                await OpenReaderAsync(page, 1440, 900);
                foreach (var direction in new[] { "rtl", "ltr" })
                {
                    await page.Locator("[data-panel=settings] [name=Direction]").EvaluateAsync("""
                        (field, value) => { field.value = value; field.dispatchEvent(new Event('input', { bubbles: true })); }
                        """, direction);
                    await page.WaitForFunctionAsync("direction => document.querySelector('.reader-book').dataset.bookDirection === direction && document.querySelector('.reader').dataset.bookState === 'idle'", direction);
                    await AssertPaperPositionsAsync(page, "1440 " + direction);
                }

                await OpenReaderAsync(page, 390, 844);
                Assert.AreEqual("1", await reader.GetAttributeAsync("data-columns"));
                // The page's outer margin keeps the text off the paper's edge, and is applied before pagination is measured.
                var leftMargin = await page.Locator(".reader__content p").First.EvaluateAsync<double>("p => { const reader = p.closest('.reader'), outer = parseFloat(getComputedStyle(reader).getPropertyValue('--reader-margin-outer')) || 0; return p.getBoundingClientRect().left - p.closest('.reader__paper').getBoundingClientRect().left >= outer - 1 ? outer : -1; }");
                Assert.IsTrue(leftMargin >= 20, "Single-page text inset must be applied before pagination is measured.");
                await Assertions.Expect(page.Locator("[data-role=pages]")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^(?:Pages 1–1 of \d+|Ch\. 1–1/\d+)$"));
                await AssertPaperPositionsAsync(page, "390 single page");
                await Evidence.SaveAsync(page, "reader-v3-single-390-light");

                // Font family and size, text width, spacing and hyphenation belong to the profile: gone from this device's storage, they come back from the server.
                await OpenReaderAsync(page, 1440, 900);
                await page.Locator("[data-action=settings]").ClickAsync();
                await Evidence.SaveAsync(page, "reader-v3-settings-1440-light");
                var saved = await page.RunAndWaitForResponseAsync(() => page.Locator("[data-panel=settings]").EvaluateAsync("""
                    panel => {
                        const set = (name, apply) => { const field = panel.querySelector(`[name=${name}]`); apply(field); field.dispatchEvent(new Event('input', { bubbles: true })); };
                        set('Size', field => { field.value = '26'; });
                        set('Paragraph', field => { field.value = '0'; });
                        set('Hyphenation', field => { field.checked = false; });
                        set('FontFamily', field => { field.value = 'sans'; });
                    }
                    """), response => response.Request.Method == "PUT" && response.Url.EndsWith("/api/v1/reading/settings", StringComparison.Ordinal));
                Assert.IsTrue(saved.Ok);
                StringAssert.Contains(saved.Request.PostData!, "\"hyphenation\":false");
                StringAssert.Contains(saved.Request.PostData!, "\"size\":26");
                Assert.AreEqual("0px", await page.Locator("[data-role=content] p").First.EvaluateAsync<string>("el => getComputedStyle(el).marginTop"));
                await page.EvaluateAsync("() => localStorage.removeItem('jularr.reader')");
                await OpenReaderAsync(page, 1440, 900);
                await page.Locator("[data-action=settings]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=settings] [name=Size]")).ToHaveValueAsync("26");
                await Assertions.Expect(page.Locator("[data-panel=settings] [name=Paragraph]")).ToHaveValueAsync("0");
                await Assertions.Expect(page.Locator("[data-panel=settings] [name=FontFamily]")).ToHaveValueAsync("sans");
                await Assertions.Expect(page.Locator("[data-panel=settings] [name=Hyphenation]")).Not.ToBeCheckedAsync();
                Assert.AreEqual("26px", await reader.EvaluateAsync<string>("el => el.style.getPropertyValue('--reader-size')"));

                // The dark double page.
                await page.EvaluateAsync("() => localStorage.setItem('jularr-ui-mode', 'dark')");
                await OpenReaderAsync(page, 1440, 900);
                await Evidence.SaveAsync(page, "reader-v3-double-1440-dark");
                await OpenReaderAsync(page, 390, 844);
                Assert.AreEqual("1", await reader.GetAttributeAsync("data-columns"));
                Assert.AreEqual("26px", await reader.EvaluateAsync<string>("el => el.style.getPropertyValue('--reader-size')"),
                    "The server-backed profile font size must remain after switching device viewport sizes.");
                await Evidence.SaveAsync(page, "reader-v3-single-390-dark");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-v3-double-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ReadAloud_HighlightsWordAndSentence_FollowsInAScrolledText_SleepTimerStops()
        {
            using var folders = new Folders("Books");
            WriteReaderEpub(Path.Combine(folders.Library, "Books", "reader-v3.epub"), "The Midnight Archive", "Local fixture", 6, 36);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            // Deterministic voice callbacks, as the speech path test has them, with word boundaries on demand.
            await page.AddInitScriptAsync("""
                const synth = new EventTarget();
                let active;
                Object.assign(synth, {
                    getVoices: () => [{voiceURI:'local:en', name:'Fixture English', lang:'en-US', localService:true, default:true}],
                    cancel() { active = undefined; },
                    speak(message) { active = message; setTimeout(() => message.onstart?.({}), 0); }
                });
                Object.defineProperty(window, 'speechSynthesis', {value:synth, configurable:true});
                Object.defineProperty(window, 'SpeechSynthesisUtterance', {value:class { constructor(text) { this.text = text; } }, configurable:true});
                window.__speech = {
                    boundary(charIndex, charLength) { active?.onboundary?.({charIndex, charLength}); },
                    finish() { const message = active; active = undefined; message?.onend?.({}); }
                };
                """);
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                await OpenReaderAsync(page, 1440, 900);
                var reader = page.Locator(".reader");

                // A scrolled text: the voice's line stays in view without anybody touching it.
                // The reading-mode button in the bar: book → scrolling, its name says so.
                var mode = page.Locator(".reader__top [data-reader-mode]");
                await Assertions.Expect(mode).ToHaveAttributeAsync("aria-label", "Reading mode: Book");
                await mode.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-mode", "scroll");
                await Assertions.Expect(mode).ToHaveAttributeAsync("aria-label", "Reading mode: Scroll");
                var toggle = page.Locator("[data-speech-toggle]");
                await Assertions.Expect(toggle).ToBeEnabledAsync();
                await toggle.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "speaking");
                // The fake synthesis schedules onstart in a later task. Let that
                // initial whole-word highlight occur before sending a finer boundary;
                // otherwise onstart can race and overwrite the expected "Cha".
                await page.WaitForFunctionAsync("() => CSS.highlights.get('reader-speech-word')?.size > 0");
                await page.EvaluateAsync("() => window.__speech.boundary(0, 3)");
                await page.WaitForFunctionAsync("() => [...(CSS.highlights.get('reader-speech-word') ?? [])][0]?.toString() === 'Cha' && CSS.highlights.get('reader-speech-sentence')?.size > 0");
                var marked = await page.EvaluateAsync<string[]>("() => [[...CSS.highlights.get('reader-speech-word')][0].toString(), [...CSS.highlights.get('reader-speech-sentence')][0].toString()]");
                Assert.AreEqual("Cha", marked[0], "The word follows the voice.");
                Assert.IsTrue(marked[1].StartsWith("Chapter One", StringComparison.Ordinal) && marked[1].Length > marked[0].Length, "The sentence around it is marked, too.");
                await Evidence.SaveAsync(page, "reader-v3-read-aloud-scroll-light");

                // Paragraph after paragraph, the text scrolls along.
                var top = await page.Locator("[data-role=content]").EvaluateAsync<double>("el => el.scrollTop");
                await page.EvaluateAsync("() => { for (let i = 0; i < 24; i++) { window.__speech.finish(); window.__speech.boundary(0, 3); } }");
                await page.WaitForFunctionAsync($"() => document.querySelector('[data-role=content]').scrollTop > {top + 200}");
                await page.WaitForFunctionAsync("""
                    () => {
                        const content = document.querySelector('[data-role=content]').getBoundingClientRect();
                        const word = [...CSS.highlights.get('reader-speech-word')][0].getBoundingClientRect();
                        return word.top >= content.top && word.bottom <= content.bottom;
                    }
                    """, null, new() { Timeout = 10_000 });

                // The floating bar sits over the lower edge of the pages, centred, above the bottom bar.
                var transport = (await page.Locator("[data-speech-transport]").BoundingBoxAsync())!;
                var canvas = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                var footer = (await page.Locator(".reader__footer").BoundingBoxAsync())!;
                Assert.IsTrue(Math.Abs(transport.X + transport.Width / 2 - (canvas.X + canvas.Width / 2)) <= 2, "The bar is centred.");
                Assert.IsTrue(transport.Y + transport.Height <= footer.Y, "The bar floats above the bottom bar.");
                Assert.IsTrue(transport.Y + transport.Height > canvas.Y + canvas.Height - 40, "The bar sits at the lower edge of the pages.");

                // The sleep timer: chosen once, shown while it runs, and reading aloud stops when it has run out.
                await page.Clock.InstallAsync();
                await page.Locator("[data-speech-transport] [data-reader-open-panel=timer]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=timer]")).ToBeVisibleAsync();
                await page.Locator("[data-panel=timer] [name=Minutes]").SelectOptionAsync("10");
                await Assertions.Expect(page.Locator("[data-speech-timer-left]")).ToHaveTextAsync("10 min");
                await Evidence.SaveAsync(page, "reader-v3-sleep-timer-light");
                await page.Clock.FastForwardAsync(11 * 60 * 1000);
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "idle");
                await Assertions.Expect(page.Locator("[data-speech-transport]")).ToBeHiddenAsync();
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-v3-read-aloud-failed");
                throw;
            }
        }

        private static async Task OpenReaderAsync(IPage page, int width, int height)
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/Read/1");
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.bookReady === 'true'", null, new() { Timeout = 30_000 });
            await page.WaitForTimeoutAsync(400);
        }

        // The bars as they stand: which tools are hidden, which of More's stand-ins show, and whether anything reaches beyond the window.
        private static Task<JsonElement> BarsAsync(IPage page) => page.EvaluateAsync<JsonElement>("""
            () => {
                const top = document.querySelector('.reader__top'), footer = document.querySelector('.reader__footer');
                const tools = [...top.querySelectorAll('[data-overflow]')];
                return {
                    present: tools.map(tool => tool.dataset.overflow),
                    hidden: tools.filter(tool => tool.hidden).map(tool => tool.dataset.overflow),
                    footerHidden: [...footer.querySelectorAll('[data-overflow]')].filter(tool => tool.hidden).map(tool => tool.dataset.overflow),
                    entries: [...document.querySelectorAll('[data-overflow-for]')].filter(entry => !entry.hidden).map(entry => entry.dataset.overflowFor),
                    topFits: top.scrollWidth <= top.clientWidth + 1,
                    footerFits: footer.scrollWidth <= footer.clientWidth + 1
                };
            }
            """);

        // The text box and the number of each page sit where its own paper is, on the left page as on the right one.
        private static async Task AssertPaperPositionsAsync(IPage page, string view)
        {
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle' && document.querySelectorAll('.reader-book__print > .reader__page-print').length >= 1");
            var sides = await page.EvaluateAsync<JsonElement>("""
                () => {
                    const reader = document.querySelector('.reader'), columns = Number(reader.dataset.columns);
                    // The text starts at the page's own margin: the outer one on the left page, the one at the spine on the right.
                    const margin = side => parseFloat(getComputedStyle(reader).getPropertyValue(columns === 1 || side === 'left' ? '--reader-margin-outer' : '--reader-margin-inner')) || 0;
                    const measure = side => {
                        const sheet = document.querySelector(`[data-book-sheet=${side}]`).getBoundingClientRect();
                        const print = document.querySelector(`[data-book-page=${side}] .reader__page-print`);
                        if (!print) return null;
                        const text = [...print.querySelectorAll('span')].filter(span => span.textContent.trim()).map(span => span.getBoundingClientRect());
                        const number = print.querySelector('[data-book-number]').getBoundingClientRect();
                        return { sheetLeft: sheet.left, width: sheet.width, textLeft: Math.min(...text.map(rect => rect.left)) - sheet.left,
                            // The print stands at the page's margin, moved by at most half of what the stack takes from the paper's width.
                            inPrint: Math.min(...text.map(rect => rect.left)) - print.getBoundingClientRect().left, shift: print.getBoundingClientRect().left - sheet.left - margin(side),
                            numberCentre: (number.left + number.right) / 2 - (sheet.left + sheet.width / 2) };
                    };
                    return { columns, left: columns === 2 ? measure('left') : null, right: measure('right') };
                }
                """);
            var right = sides.GetProperty("right");
            Assert.IsTrue(Math.Abs(right.GetProperty("numberCentre").GetDouble()) <= 2, $"{view}: the right page's number is centred on its paper ({right.GetProperty("numberCentre").GetDouble():F1}).");
            if (sides.GetProperty("columns").GetInt32() == 2)
            {
                var left = sides.GetProperty("left");
                Assert.IsTrue(Math.Abs(left.GetProperty("numberCentre").GetDouble()) <= 2, $"{view}: the left page's number is centred on its paper ({left.GetProperty("numberCentre").GetDouble():F1}).");
                Assert.IsTrue(Math.Abs(left.GetProperty("inPrint").GetDouble() - right.GetProperty("inPrint").GetDouble()) <= 2,
                    $"{view}: both text boxes start the same way in their print (left {left.GetProperty("inPrint").GetDouble():F1}, right {right.GetProperty("inPrint").GetDouble():F1}).");
                foreach (var side in new[] { left, right })
                    Assert.IsTrue(Math.Abs(side.GetProperty("shift").GetDouble()) <= 12, $"{view}: each print stands at its page's margin ({side.GetProperty("shift").GetDouble():F1}).");
            }

            Assert.IsTrue(right.GetProperty("textLeft").GetDouble() > 8, $"{view}: the text keeps a margin.");
        }

        // A small synthetic book: chapters of ordinary paragraphs, one with words long enough to hyphenate.
        internal static string WriteReaderEpub(string file, string title, string author, int chapters, int paragraphs)
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetype.Open()))
            {
                writer.Write("application/epub+zip");
            }

            Entry(archive, "META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            var names = new[] { "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight" };
            var manifest = string.Concat(Enumerable.Range(0, chapters).Select(index => $"""<item id="c{index}" href="c{index}.xhtml" media-type="application/xhtml+xml"/>"""));
            var spine = string.Concat(Enumerable.Range(0, chapters).Select(index => $"""<itemref idref="c{index}"/>"""));
            Entry(archive, "OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">reader-v3</dc:identifier><dc:title>{SecurityElement.Escape(title)}</dc:title><dc:creator>{SecurityElement.Escape(author)}</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-01-01T00:00:00Z</meta></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{manifest}</manifest><spine>{spine}</spine></package>""");
            var entries = string.Concat(Enumerable.Range(0, chapters).Select(index => $"""<li><a href="c{index}.xhtml">Chapter {names[index]}</a></li>"""));
            Entry(archive, "OEBPS/nav.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol>{entries}</ol></nav></body></html>""");
            for (var index = 0; index < chapters; index++)
            {
                var text = string.Concat(Enumerable.Range(0, paragraphs).Select(number =>
                    "<p>" + Sentences[(number + index) % Sentences.Length] + " " + Sentences[(number + index + 2) % Sentences.Length] + "</p>"));
                Entry(archive, $"OEBPS/c{index}.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter {names[index]}</title></head><body><h1>Chapter {names[index]}</h1>{text}</body></html>""");
            }

            return file;
        }

        private static void Entry(ZipArchive archive, string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
    }
}
