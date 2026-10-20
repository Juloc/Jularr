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
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Download and activate"));
                await Assertions.Expect(page.GetByText("Available to devices", new() { Exact = true })).ToBeVisibleAsync();
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
                await page.GotoAsync("/Admin/Instance");
                await page.GetByRole(AriaRole.Link, new() { Name = "Speech models", Exact = true }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Admin/Instance/Speech$"));
                var rows = page.Locator(".page-table tbody tr");
                await Assertions.Expect(rows).ToHaveCountAsync(3);
                await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("de-DE");
                await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("id-ID");
                await Assertions.Expect(rows.Nth(2)).ToContainTextAsync("en-US");
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download and activate", Exact = true })).ToHaveCountAsync(3);
                await Assertions.Expect(page.GetByText("Not activated", new() { Exact = true })).ToHaveCountAsync(3);
                await rows.Nth(1).GetByText("Package details", new() { Exact = true }).ClickAsync();
                await Assertions.Expect(rows.Nth(1).Locator("details")).ToHaveAttributeAsync("open", "");
                await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("634b0169fe41c368e9fafe128f30647f9e0301e852ce503cd78146db524f1f12");
                await Assertions.Expect(rows.Nth(1).GetByRole(AriaRole.Button, new() { Name = "Download and activate", Exact = true })).ToBeInViewportAsync();
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
    }
}
