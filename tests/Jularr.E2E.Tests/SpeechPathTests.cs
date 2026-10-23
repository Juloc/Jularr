using Microsoft.Playwright;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class SpeechPathTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task ReadAloud_UsesProfilePreferencesSelectionWordPaintAndCancellableDeviceSessions()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "speech.epub"), "Speech book", "Local fixture");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            // Real profile/Reader HTML and APIs; deterministic platform callbacks, not an audible CI speaker.
            await page.AddInitScriptAsync("""
                const synth = new EventTarget();
                const messages = [];
                let active;
                Object.assign(synth, {
                    getVoices: () => [
                        {voiceURI:'local:en', name:'Fixture English', lang:'en-US', localService:true, default:true},
                        {voiceURI:'local:de', name:'Fixture Deutsch', lang:'de-DE', localService:true, default:false}
                    ],
                    cancel() { active = undefined; },
                    speak(message) {
                        active = message;
                        messages.push({text:message.text, rate:message.rate, voice:message.voice?.voiceURI});
                        setTimeout(() => message.onstart?.({}), 0);
                    }
                });
                Object.defineProperty(window, 'speechSynthesis', {value:synth, configurable:true});
                Object.defineProperty(window, 'SpeechSynthesisUtterance', {value:class { constructor(text) { this.text = text; } }, configurable:true});
                window.__speech = {messages, finish() { const message = active; active = undefined; message?.onend?.({}); }};
                """);
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Settings");
                var fields = page.Locator("[data-speech-settings]");
                await Assertions.Expect(fields.Locator("[name=VoiceId] option[value='local:en']")).ToHaveCountAsync(1);
                await fields.Locator("[name=VoiceId]").SelectOptionAsync("local:en");
                await fields.Locator("[name=Rate]").EvaluateAsync("input => { input.value='1.25'; input.dispatchEvent(new Event('input', {bubbles:true})); input.dispatchEvent(new Event('change', {bubbles:true})); }");
                await Steps.SaveAsync(page);
                await page.ReloadAsync();
                await Assertions.Expect(fields.Locator("[name=VoiceId]")).ToHaveValueAsync("local:en");
                await Assertions.Expect(fields.Locator("[name=Rate]")).ToHaveValueAsync("1.25");
                await Evidence.SaveAsync(page, "speech-profile-settings");

                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await page.GotoAsync("/Admin/Library");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Scan Books"));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    await page.GotoAsync("/Admin/Library/Unmatched");
                    if (await page.Locator("form [name=Title]").CountAsync() > 0) break;
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The local speech EPUB was not scanned.");
                    await Task.Delay(500);
                }
                await page.Locator("form [name=Title]").FillAsync("Speech book");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Create work and identify"));
                await page.GotoAsync("/Read/1");
                var reader = page.Locator(".reader");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true'");
                var canvasWidth = await page.Locator(".reader__canvas").EvaluateAsync<int>("el => Math.round(el.getBoundingClientRect().width)");
                var toggle = page.Locator("[data-speech-toggle]");
                await Assertions.Expect(toggle).ToBeEnabledAsync();
                await toggle.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "speaking");
                Assert.AreEqual(canvasWidth, await page.Locator(".reader__canvas").EvaluateAsync<int>("el => Math.round(el.getBoundingClientRect().width)"), "Speech controls must not shift the book layout.");
                await page.WaitForFunctionAsync("() => CSS.highlights?.get('reader-speech-word') && [...CSS.highlights.get('reader-speech-word')].some(range => range.startContainer.parentElement.closest('.reader__page-print'))");
                Assert.AreEqual(1.25m, await page.EvaluateAsync<decimal>("window.__speech.messages[0].rate"));
                Assert.AreEqual("local:en", await page.EvaluateAsync<string>("window.__speech.messages[0].voice"));
                await Evidence.SaveAsync(page, "reader-read-aloud-word");
                await page.Locator("[data-reader-fullscreen]").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-view", "fullscreen");
                await Assertions.Expect(page.Locator("[data-speech-transport]")).ToBeVisibleAsync();
                await page.Locator(".reader__reveal--bottom").ClickAsync();
                await page.Locator("[data-reader-fullscreen]").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-view", "normal");
                await page.Locator("[data-speech-pause]").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "paused");
                await page.Locator("[data-speech-pause]").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "speaking");

                // Lazy paragraph continuation follows the real paged content, not a separate speech progress store.
                await page.EvaluateAsync("() => { for (let i=0;i<25;i++) window.__speech.finish(); }");
                await page.WaitForFunctionAsync("() => Number(document.querySelector('.reader').dataset.page) > 1");
                await page.Locator("[data-speech-stop]").ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "idle");
                await Assertions.Expect(page.Locator("[data-speech-transport]")).ToBeHiddenAsync();

                // Selected text is read once. Highlighting never inserts marks or changes the original text.
                await page.Locator("[data-reader-select]").ClickAsync();
                await page.EvaluateAsync("""
                    () => {
                        const paragraph = document.querySelector('[data-role=content] p');
                        const node = document.createTreeWalker(paragraph, NodeFilter.SHOW_TEXT).nextNode();
                        const range = document.createRange(); range.setStart(node,0); range.setEnd(node,11);
                        const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
                    }
                    """);
                await toggle.ClickAsync();
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "speaking");
                Assert.AreEqual("Paragraph 1", await page.EvaluateAsync<string>("window.__speech.messages.at(-1).text"));
                await page.EvaluateAsync("window.__speech.finish()");
                await Assertions.Expect(reader).ToHaveAttributeAsync("data-speech-state", "idle");
                Assert.AreEqual(0, await page.Locator("[data-role=content] mark").CountAsync());
                await page.Locator("[data-reader-select]").ClickAsync();

                await page.SetViewportSizeAsync(390, 844);
                await Assertions.Expect(toggle).ToBeInViewportAsync();
                await toggle.ClickAsync();
                await Assertions.Expect(page.Locator("[data-speech-transport]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-read-aloud-mobile");
                await page.Locator("[data-speech-stop]").ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "More", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Voice and speed", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=speech]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-panel=speech] [name=VoiceId]")).ToHaveValueAsync("local:en");
                await Assertions.Expect(page.Locator("[data-panel=speech] [data-form-save]")).ToBeVisibleAsync();
                await page.Locator("[data-panel=speech] [name=Rate]").EvaluateAsync("input => { input.value='1.4'; input.dispatchEvent(new Event('input', {bubbles:true})); }");
                await Steps.SubmitAsync(page, page.Locator("[data-panel=speech] form"));
                await Assertions.Expect(page.Locator("[data-speech-toggle]")).ToBeEnabledAsync();
                Assert.AreEqual(1.4m, await page.Locator(".reader").EvaluateAsync<decimal>("el => JSON.parse(el.dataset.speechPreferences).rate"));
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-read-aloud-failed");
                throw;
            }
        }


        [TestMethod]
        [Timeout(180_000)]
        public async Task Neural_ReadAloudUsesExplicitDeviceInstallBoundProfileControlsAndVerifiedCache()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "neural.epub"), "Neural book", "Local fixture");
            var speechPath = Path.Combine(folders.Fixtures, "speech");
            Directory.CreateDirectory(speechPath);
            var package = await File.ReadAllBytesAsync(Path.Combine(JularrInstance.Repository, "tests", "Jularr.E2E.Tests", "Fixtures", "speech-model.tar.bz2"));
            var hash = Convert.ToHexStringLower(SHA256.HashData(package));
            await File.WriteAllBytesAsync(Path.Combine(speechPath, hash + ".tar.bz2"), package);
            await File.WriteAllTextAsync(Path.Combine(speechPath, "catalog.json"), JsonSerializer.Serialize(new { models = new[] { new {
                id = "fixture-neural", version = "fixture", name = "Fixture neural", language = "en-US", voice = "0", license = "MIT (synthetic fixture)",
                licenseUrl = "https://github.com/cscott/seek-bzip/blob/v2.0.0/LICENSE", source = "https://not-requested.invalid/model.tar.bz2", sizeBytes = package.Length, sha256 = hash
            } } }));
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library, ["Media:SpeechPath"] = speechPath });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            var modelRequests = 0;
            var runtimeRequests = 0;
            page.Request += (_, request) =>
            {
                if (request.Url.EndsWith("/speech/models/" + hash + "/file", StringComparison.Ordinal)) Interlocked.Increment(ref modelRequests);
                if (request.Url.EndsWith(".wasm", StringComparison.Ordinal) || request.Url.EndsWith(".data", StringComparison.Ordinal)) Interlocked.Increment(ref runtimeRequests);
            };
            // Real archive worker, storage, profile API and Reader; inference is explicitly doubled, not an audible model test.
            await page.AddInitScriptAsync("""
                const NativeWorker = Worker;
                window.__neuralLoads = 0;
                window.Worker = class extends NativeWorker {
                    postMessage(message, transfer) {
                        if (message.type === 'load') { window.__neuralLoads++; queueMicrotask(() => this.dispatchEvent(new MessageEvent('message', {data:{type:'ready'}}))); return; }
                        if (message.type === 'generate') { queueMicrotask(() => this.dispatchEvent(new MessageEvent('message', {data:{type:'audio',id:message.id,sampleRate:22050,samples:new Float32Array(220500)}}))); return; }
                        super.postMessage(message, transfer);
                    }
                };
                """);
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Instance/Speech");

                // Activation is the switch of the model's row, saved by the pill (saving downloads the package).
                var model = page.Locator(".speech-models .list-row").First;
                await model.GetByRole(AriaRole.Switch, new() { Name = "Available to devices" }).CheckAsync(new() { Force = true });
                await Steps.SaveAsync(page);
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator(".speech-models .list-row").First.GetByRole(AriaRole.Switch, new() { Name = "Available to devices" })).ToBeCheckedAsync();
                await page.GotoAsync("/Settings");
                Assert.AreEqual(0, modelRequests);
                Assert.AreEqual(0, runtimeRequests);
                var fields = page.Locator("[data-speech-settings]");
                await fields.EvaluateAsync("element => element.scrollIntoView({block:'center'})");
                await fields.GetByRole(AriaRole.Radio, new() { Name = "Offline neural", Exact = true }).CheckAsync();
                await fields.Locator("[name=ModelId]").SelectOptionAsync("fixture-neural");
                await Assertions.Expect(fields.Locator("[name=VoiceId]")).ToHaveValueAsync("0");
                Assert.IsTrue(await fields.Locator("[name=Pitch]").EvaluateAsync<bool>("input => input.inert"));
                Assert.IsTrue(await fields.Locator("form").EvaluateAsync<bool>("form => new FormData(form).has('Pitch')"), "The unsupported pitch remains bound, not discarded.");
                await Assertions.Expect(fields.Locator("[data-speech-model-license]")).ToContainTextAsync("MIT");
                await fields.Locator("[data-speech-install]").ClickAsync();
                await Assertions.Expect(fields.Locator("[data-speech-model-status]")).ToContainTextAsync("Installed on this device");
                Assert.AreEqual(1, modelRequests);
                Assert.AreEqual(0, runtimeRequests);
                await Steps.SaveAsync(page);
                await Assertions.Expect(fields.Locator("[name=ModelId]")).ToHaveValueAsync("fixture-neural");
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await page.GotoAsync("/Admin/Library");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Scan Books"));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    await page.GotoAsync("/Admin/Library/Unmatched");
                    if (await page.Locator("form [name=Title]").CountAsync() > 0) break;
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The neural speech EPUB was not scanned.");
                    await Task.Delay(500);
                }
                await page.Locator("form [name=Title]").FillAsync("Neural book");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Create work and identify"));
                await page.GotoAsync("/Read/1");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true'");
                await page.Locator("[data-speech-toggle]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader")).ToHaveAttributeAsync("data-speech-state", "speaking");
                Assert.AreEqual(1, await page.EvaluateAsync<int>("window.__neuralLoads"));
                await page.WaitForFunctionAsync("() => [...CSS.highlights.get('reader-speech-word')].some(range => range.toString().length > 10)");
                await page.Locator("[data-speech-pause]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader")).ToHaveAttributeAsync("data-speech-state", "paused");
                await page.Locator("[data-speech-pause]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader")).ToHaveAttributeAsync("data-speech-state", "speaking");
                await Evidence.SaveAsync(page, "reader-neural-section-highlight");
                await page.Locator("[data-speech-transport] [data-reader-open-panel=speech]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=speech] [name=Pitch]")).ToHaveAttributeAsync("aria-disabled", "true");
                await Evidence.SaveAsync(page, "reader-neural-model-settings");
                await page.Locator("[data-panel=speech] [data-speech-remove]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader")).ToHaveAttributeAsync("data-speech-state", "idle");
                await page.Locator("[data-reader-close=speech]").ClickAsync();
                await page.Locator("[data-speech-toggle]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader")).ToHaveAttributeAsync("data-speech-state", "failed");
                await Assertions.Expect(page.Locator("[data-speech-status]")).ToContainTextAsync("not installed on this device");
                Assert.AreEqual(1, modelRequests, "Missing or removed models are never silently downloaded.");
            }
            catch
            {
                await Evidence.SaveAsync(page, "reader-neural-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(120_000)]
        public async Task Models_OpenFromModuleAdministrationWithoutInstallingAnything()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Instance/Modules");
                await page.GetByRole(AriaRole.Link, new() { Name = "Speech models", Exact = true }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Admin/Instance/Speech$"));
                var rows = page.Locator(".speech-models .list-body .list-row");
                await Assertions.Expect(rows).ToHaveCountAsync(3);
                await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("de-DE");
                await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("id-ID");
                await Assertions.Expect(rows.Nth(2)).ToContainTextAsync("en-US");
                await Assertions.Expect(page.GetByRole(AriaRole.Switch, new() { Name = "Available to devices" })).ToHaveCountAsync(3);
                await Assertions.Expect(page.Locator(".speech-models input[name=IsEnabled]:checked")).ToHaveCountAsync(0);
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download and activate", Exact = true })).ToHaveCountAsync(0);
                await rows.Nth(1).GetByRole(AriaRole.Link, new() { Name = "Package details", Exact = true }).ClickAsync();
                var drawer = page.GetByRole(AriaRole.Dialog);
                await Assertions.Expect(drawer).ToBeVisibleAsync();
                await Assertions.Expect(drawer).ToContainTextAsync("634b0169fe41c368e9fafe128f30647f9e0301e852ce503cd78146db524f1f12");
                await drawer.GetByRole(AriaRole.Link, new() { Name = "Close", Exact = true }).ClickAsync();
                await Evidence.SaveAsync(page, "speech-models-desktop");
                await page.SetViewportSizeAsync(390, 844);
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Modules", Exact = true })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "speech-models-mobile");
            }
            catch
            {
                await Evidence.SaveAsync(page, "speech-models-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(120_000)]
        public async Task Catalog_SearchPagingSamplesAndLicenseReviewWorkOnDesktopAndPhone()
        {
            var speech = Directory.CreateTempSubdirectory("jularr-speech-catalog-e2e-").FullName;
            try
            {
                // Synthetic metadata only; no external package or inference runtime is needed.
                var models = Enumerable.Range(1, 28).Select(index => new
                {
                    id = "catalog-fixture-" + index, version = "1", name = "Catalogue fixture " + index.ToString("00"), language = index == 1 ? "en-US" : "de-DE", voice = "0",
                    gender = index == 28 ? 2 : index == 27 ? 1 : 0, genderSource = index >= 27 ? "https://example.test/synthetic-gender" : null,
                    license = "Review model card", licenseUrl = "https://example.test/model-card", source = "https://example.test/package.tar.bz2", sizeBytes = 1002,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("catalog-fixture-" + index))), requiresLicenseAcceptance = true,
                    sampleUrl = "https://k2-fsa.github.io/sherpa/onnx/tts/all/piper/mp3/de_DE/fixture-" + index + "/0.mp3"
                }).ToArray();
                await File.WriteAllTextAsync(Path.Combine(speech, "catalog.json"), JsonSerializer.Serialize(new { models }));
                await File.WriteAllBytesAsync(Path.Combine(speech, models[26].sha256 + ".tar.bz2"), new byte[1002]);
                await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Media:SpeechPath"] = speech });
                await using var session = await BrowserSession.StartAsync(jularr);
                var page = session.Page;
                try
                {
                    var samples = 0;
                    await page.RouteAsync("https://k2-fsa.github.io/sherpa/onnx/tts/all/piper/mp3/**", route =>
                    {
                        samples++;
                        return route.FulfillAsync(new RouteFulfillOptions { ContentType = "audio/wav", BodyBytes = SilentSample() });
                    });
                    await Owner.SetUpAndSignInAsync(page);
                    await page.GotoAsync("/Admin/Instance/Speech");
                    var rows = page.Locator(".speech-models .list-body .list-row");
                    await Assertions.Expect(rows).ToHaveCountAsync(25);
                    Assert.AreEqual(0, samples, "Opening a catalog must not load audio.");
                    var first = rows.Nth(0).Locator("[data-speech-sample]");
                    await first.ClickAsync();
                    await Assertions.Expect(first).ToHaveAttributeAsync("aria-pressed", "true");
                    await Assertions.Expect(first).ToHaveTextAsync("Stop sample");
                    var second = rows.Nth(1).Locator("[data-speech-sample]");
                    await second.ClickAsync();
                    await Assertions.Expect(first).ToHaveAttributeAsync("aria-pressed", "false");
                    await Assertions.Expect(second).ToHaveAttributeAsync("aria-pressed", "true");
                    await second.ClickAsync();
                    await Assertions.Expect(second).ToHaveAttributeAsync("aria-pressed", "false");
                    await Evidence.SaveAsync(page, "speech-catalog-desktop");

                    // The shared pager: a full first page has no way back and a way on; the short second page the other way round.
                    var pager = page.GetByRole(AriaRole.Navigation, new() { Name = "Pages", Exact = true });
                    await Assertions.Expect(pager).ToContainTextAsync("Page 1");
                    await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true })).ToBeDisabledAsync();
                    await page.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).ClickAsync();
                    await Assertions.Expect(rows).ToHaveCountAsync(3);
                    await Assertions.Expect(pager).ToContainTextAsync("Page 2");
                    await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Previous", Exact = true })).ToBeVisibleAsync();
                    await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();
                    async Task ChooseAsync(string label, string field, string value)
                    {
                        var select = page.Locator("#choice-" + field);
                        var optionLabel = await select.Locator("option[value='" + value + "']").TextContentAsync();
                        await page.Locator("#choice-" + field + "-button").ClickAsync();
                        await page.GetByRole(AriaRole.Listbox, new() { Name = label, Exact = true }).GetByRole(AriaRole.Option, new() { Name = optionLabel!, Exact = true }).ClickAsync();
                        await page.WaitForURLAsync(url => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)[field] == value);
                        await Assertions.Expect(select).ToHaveValueAsync(value);
                    }

                    await Assertions.Expect(page.Locator(".speech-models-toolbar select option[value='']")).ToHaveCountAsync(3);
                    await ChooseAsync("Language", "language", "de-DE");
                    await ChooseAsync("Voice gender", "gender", "1");
                    await ChooseAsync("Installation on server", "isInstalled", "true");
                    await Evidence.SaveAsync(page, "speech-catalog-filters");
                    await Assertions.Expect(rows).ToHaveCountAsync(1);
                    await Assertions.Expect(rows).ToContainTextAsync("fixture 27");
                    var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search voices, languages or quality…", Exact = true });
                    await search.FillAsync("fixture 27");
                    await search.PressAsync("Enter");
                    await page.WaitForURLAsync(url => new Uri(url).Query.Contains("q=fixture+27", StringComparison.Ordinal));
                    await Assertions.Expect(rows).ToHaveCountAsync(1);
                    await Assertions.Expect(search).ToHaveValueAsync("fixture 27");
                    await Assertions.Expect(page.Locator("#choice-gender")).ToHaveValueAsync("1");
                    await ChooseAsync("Language", "language", "");
                    await ChooseAsync("Voice gender", "gender", "");
                    await ChooseAsync("Installation on server", "isInstalled", "");
                    await Assertions.Expect(rows).ToHaveCountAsync(1);
                    await search.FillAsync("fixture 28");
                    await search.PressAsync("Enter");
                    await page.WaitForURLAsync(url => new Uri(url).Query.Contains("q=fixture+28", StringComparison.Ordinal));
                    await Assertions.Expect(rows).ToHaveCountAsync(1);
                    await Assertions.Expect(search).ToHaveValueAsync("fixture 28");

                    // A model with conditions asks to accept them beside its switch in the row; saving without that keeps it off and says so in the row.
                    await Assertions.Expect(rows.GetByRole(AriaRole.Link, new() { Name = "Review model conditions", Exact = true })).ToBeVisibleAsync();
                    await rows.GetByRole(AriaRole.Switch, new() { Name = "Available to devices" }).CheckAsync(new() { Force = true });
                    await Assertions.Expect(page.Locator("[data-save-pill]")).ToContainTextAsync("1 change");
                    await Steps.FollowAsync(page, page.Locator("[data-save-pill] [data-save-submit]"));
                    await Assertions.Expect(rows).ToContainTextAsync("Review and confirm this model's conditions");
                    await Assertions.Expect(rows.Locator("[name=AcceptLicense][type=checkbox]")).Not.ToBeCheckedAsync();
                    await page.SetViewportSizeAsync(390, 844);
                    await Evidence.SaveAsync(page, "speech-catalog-license-phone");
                    await Assertions.Expect(rows).ToHaveCountAsync(1);
                    Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth + 1"), "The catalog must fit on a phone.");
                    await Evidence.SaveAsync(page, "speech-catalog-phone");
                    Assert.AreEqual(1, Directory.EnumerateFiles(speech, "*.tar.bz2").Count(), "Only the explicitly supplied synthetic installed fixture exists.");
                    if (Environment.GetEnvironmentVariable("JULARR_SPEECH_LIVE_METADATA") == "1")
                    {
                        // Explicit manual replay of the real sources; still no package downloads.
                        await File.WriteAllTextAsync(Path.Combine(speech, "catalog.json"), "{\"models\":[]}");
                        await page.SetViewportSizeAsync(1440, 1000);
                        await page.GotoAsync("/Admin/Instance/Speech");
                        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh voice catalog", Exact = true }).ClickAsync();
                        await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30000 });
                        await search.FillAsync("de-DE");
                        await search.PressAsync("Enter");
                        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("q=de-DE", StringComparison.Ordinal));
                        await Assertions.Expect(rows.Filter(new() { HasText = "eva_k" })).ToHaveCountAsync(1, new() { Timeout = 10000 });
                        await Assertions.Expect(search).ToHaveValueAsync("de-DE");
                        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(speech, "discovered.json")));
                        Evidence.Log("Official metadata: " + snapshot.RootElement.GetProperty("models").GetArrayLength() + " compatible pinned packages; no weights downloaded.");
                        await Evidence.SaveAsync(page, "speech-catalog-live-german");
                        await page.SetViewportSizeAsync(390, 844);
                        await Assertions.Expect(page.Locator(".app-sidebar")).Not.ToBeVisibleAsync();
                        await Evidence.SaveAsync(page, "speech-catalog-live-phone");
                        await Assertions.Expect(page.Locator(".speech-models-toolbar select")).ToHaveCountAsync(3);
                        await Assertions.Expect(page.Locator(".speech-models-toolbar select option[value='']")).ToHaveCountAsync(3);
                        Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth + 1"));
                        await Evidence.SaveAsync(page, "speech-catalog-filters-phone");
                    }
                }
                catch (Exception exception)
                {
                    Evidence.Log("Speech catalog failure: " + exception.Message);
                    await Evidence.SaveAsync(page, "speech-catalog-failed");
                    throw;
                }
            }
            finally { Directory.Delete(speech, true); }
        }

        private static byte[] SilentSample()
        {
            using var output = new MemoryStream();
            using var wave = new BinaryWriter(output);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(160036);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16);
            wave.Write((short)1); wave.Write((short)1); wave.Write(8000); wave.Write(16000); wave.Write((short)2); wave.Write((short)16);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(160000); wave.Write(new byte[160000]);
            return output.ToArray();
        }
    }
}
