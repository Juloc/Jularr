using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using Jularr.Integrations.Logic.Translation;
using Jularr.Backend.Tests.Infrastructure;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LocalTranslationModelTests
    {
        private static readonly Uri Ollama = new("http://ollama:11434/");

        [TestMethod]
        public async Task Machine_ThatAcceptsTheConnectionButNeverAnswers_FailsItsCheckAfterTheLimit_AndIsKeptAsATimeout()
        {
            using var http = new HttpClient(SilentServer.NeverAnswers());
            var models = new OllamaLocalModels(http) { QuestionLimit = TimeSpan.FromMilliseconds(200), GenerateLimit = TimeSpan.FromMilliseconds(200) };
            var clock = new Clock();
            var machines = new LocalTranslationMachines(models, Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434" }), clock);

            // Neither a question nor a translation hangs for the client's own 10 minutes; both end as the timeout the callers already treat as a failed machine.
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await models.InstalledAsync(Ollama, default));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await models.TranslateAsync(Ollama, "translategemma:12b", "en", "de", "Hello", default));
            // A caller that cancels itself still ends as a cancellation, not as a failed machine.
            using var own = new CancellationTokenSource(50);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await models.InstalledAsync(Ollama, own.Token));

            // The check keeps it as not reachable for lack of an answer; choices pass it over, and the Reader hears that the machines are away.
            await machines.CheckAsync([], default);
            Assert.AreEqual((null, "timeout"), (machines.CheckOf(Ollama)?.Installed, machines.CheckOf(Ollama)?.Reason));
            Assert.AreEqual(clock.Now, machines.CheckOf(Ollama)?.CheckedAt);
            Assert.IsNull(machines.Choose([]));
            Assert.IsTrue(machines.AllAway([]));
        }

        [TestMethod]
        public async Task Machines_AreChosenByTheirLastCheck_InTheOwnersOrder_AndAReturnedOneCountsWithTheNextCheck()
        {
            var handler = new Machines();
            using var http = new HttpClient(handler);
            var clock = new Clock();
            var machines = new LocalTranslationMachines(new OllamaLocalModels(http),
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434" }), clock);

            // Nothing saved: the configured endpoint alone, so an upgraded install translates as before; addresses outside the private network never count.
            CollectionAssert.AreEqual(new[] { Ollama }, machines.Order([]));
            CollectionAssert.AreEqual(new[] { new Uri("http://192.168.1.20:11434/"), new Uri("http://10.0.0.2:11434/") },
                machines.Order(["http://192.168.1.20:11434", "http://8.8.8.8:11434", "http://10.0.0.2:11434", "http://192.168.1.20:11434"]));

            // A read never asks a machine: before the first check nothing is chosen. A translation about to run asks the machines never asked yet.
            string[] saved = ["http://192.168.1.20:11434", "http://10.0.0.2:11434"];
            Assert.IsNull(machines.Choose(saved));
            Assert.AreEqual(0, handler.Requests);
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), (await machines.ChooseAsync(saved, default))?.Server);
            Assert.AreEqual(4, handler.Requests, "Each machine once for its models and once for the loaded ones.");
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), (await machines.ChooseAsync(saved, default))?.Server);
            Assert.AreEqual(4, handler.Requests, "A machine asked before is not asked again by a choice.");

            // The first drops out: choices follow the check that found it, and the next takes over.
            handler.Offline = "192.168.1.20";
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), machines.Choose(saved)?.Server);
            await machines.CheckAsync(saved, default);
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), machines.Choose(saved)?.Server);
            Assert.AreEqual("unreachable", machines.CheckOf(new Uri("http://192.168.1.20:11434/"))?.Reason);
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b", "translategemma:12b" }, machines.Installed(saved));

            // Back again, it counts with the next check, without a rest of its own.
            handler.Offline = null;
            await machines.CheckAsync(saved, default);
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), machines.Choose(saved)?.Server);

            // A machine that failed a block rests a minute whatever its checks say; a machine without the wanted model is passed over.
            machines.Fail(new Uri("http://192.168.1.20:11434/"));
            await machines.CheckAsync(saved, default);
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), machines.Choose(saved)?.Server);
            Assert.IsFalse(machines.AllAway(saved));
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.AreEqual(new Uri("http://192.168.1.20:11434/"), machines.Choose(saved)?.Server);
            Assert.AreEqual(new Uri("http://10.0.0.2:11434/"), machines.Choose(saved, "translategemma:12b")?.Server);
            Assert.IsNull(machines.Choose(saved, "translategemma:27b"));
            // Within one machine prefer 12B over 4B, without changing the owner's machine priority.
            await machines.CheckAsync(["http://10.0.0.2:11434"], default);
            Assert.AreEqual("translategemma:12b", machines.Choose(["http://10.0.0.2:11434"])?.Model);

            // Reading the state asks nobody.
            var asked = handler.Requests;
            _ = (machines.Choose(saved), machines.Installed(saved), machines.AllAway(saved), machines.StateOf(new Uri("http://10.0.0.2:11434/")));
            Assert.AreEqual(asked, handler.Requests);
        }

        [TestMethod]
        public void Machines_AFailure_IsRemembered_WithItsReason_AndRestsForAMinute()
        {
            var clock = new Clock();
            using var http = new HttpClient(new Machines());
            var machines = new LocalTranslationMachines(new OllamaLocalModels(http), Options.Create(new LocalTranslationOptions()), clock);
            var server = new Uri("http://192.168.1.20:11434/");

            Assert.AreEqual((null, null, null), machines.StateOf(server));
            machines.Fail(server, LocalTranslationMachines.ReasonOf(new OperationCanceledException()));
            var failed = machines.StateOf(server);
            Assert.AreEqual(clock.Now + TimeSpan.FromMinutes(1), failed.RestingUntil);
            Assert.AreEqual("timeout", failed.Reason);
            Assert.AreEqual(clock.Now, failed.FailedAt);

            // After its rest the machine no longer rests, but the last error stays.
            clock.Now += TimeSpan.FromMinutes(2);
            var after = machines.StateOf(server);
            Assert.IsNull(after.RestingUntil);
            Assert.AreEqual("timeout", after.Reason);
            Assert.AreEqual("unreachable", LocalTranslationMachines.ReasonOf(new HttpRequestException()));
            Assert.AreEqual("invalid_answer", LocalTranslationMachines.ReasonOf(new InvalidDataException()));
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        // Two machines: 192.168.1.20 with the small model, 10.0.0.2 with the small and the middle one.
        private sealed class Machines : HttpMessageHandler
        {
            private int _requests;

            public string? Offline { get; set; }
            public int Requests => _requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                if (request.RequestUri!.Host == Offline) throw new HttpRequestException("No route to host.");
                var models = request.RequestUri.Host == "10.0.0.2" ? """[{"name":"translategemma:4b"},{"name":"translategemma:12b"}]""" : """[{"name":"translategemma:4b"}]""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":" + models + "}") });
            }
        }
        [TestMethod]
        public void Endpoint_AllowsOnlyLocalOrPrivateTargets()
        {
            foreach (var value in new[] { "http://ollama:11434", "http://127.0.0.1:11434", "http://10.0.0.4:11434", "https://192.168.1.30" })
                Assert.IsNotNull(OllamaLocalModels.Endpoint(value), value);

            foreach (var value in new[] { "https://example.org", "http://169.254.169.254", "http://8.8.8.8",
                         "http://user:pass@ollama:11434", "http://ollama:11434/other", "file:///etc/passwd" })
                Assert.IsNull(OllamaLocalModels.Endpoint(value), value);
        }

        [TestMethod]
        public async Task Client_ListsApprovedModels_AndBenchmarksWithBoundedInput()
        {
            var handler = new FakeOllama();
            using var http = new HttpClient(handler);
            var client = new OllamaLocalModels(http);
            var server = new Uri("http://ollama:11434/");
            var installed = await client.InstalledAsync(server, default);
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, installed.Keys.ToArray());
            Assert.AreEqual(new string('a', 64), installed["translategemma:4b"]);
            CollectionAssert.AreEquivalent(new[] { "translategemma:4b" }, await client.RunningAsync(server, default));
            var result = await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default);
            Assert.AreEqual("Rina ging nach Hause.", result.Text);
            Assert.AreEqual(10.0, result.TokensPerSecond);
            await client.TranslateAsync(server, "translategemma:4b", "id", "de", "Rina pulang.", default, "Rina tinggal di kota.");
            StringAssert.Contains(handler.LastPrompt!, "Earlier source passage for context only");
            StringAssert.Contains(handler.LastPrompt!, "Rina tinggal di kota.");
            Assert.IsTrue(handler.LastPrompt!.EndsWith("into German:\n\n\nRina pulang.", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "untrusted:90b", "id", "de", "Rina pulang.", default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.TranslateAsync(server, "translategemma:4b", "id", "de", new string('x', 20001), default));
        }

        [TestMethod]
        public async Task Block_TheModelTranslatesTheContextToo_IsAskedAgainWithoutIt()
        {
            var handler = new EchoingOllama();
            using var http = new HttpClient(handler);
            var client = new OllamaLocalModels(http);

            // With context the answer holds the context's translation as well: more paragraphs than the block.
            Assert.AreEqual("Rina ging nach Hause.", await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang.", "Rina tinggal di kota.", default));
            Assert.AreEqual(2, handler.Calls);
            Assert.IsFalse(handler.LastPrompt!.Contains("Earlier source passage", StringComparison.Ordinal));

            // Without context the first answer stands.
            handler.Calls = 0;
            Assert.AreEqual("Rina ging nach Hause.", await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang.", null, default));
            Assert.AreEqual(1, handler.Calls);

            // An answer that still fails the checks is none.
            handler.Bad = true;
            Assert.IsNull(await client.TranslateBlockAsync(Ollama, "translategemma:4b", "id", "de", "Rina pulang. Dia makan.", "Rina tinggal di kota.", default));
        }

        private sealed class EchoingOllama : HttpMessageHandler
        {
            public int Calls { get; set; }
            public bool Bad { get; set; }
            public string? LastPrompt { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                LastPrompt = json.RootElement.GetProperty("prompt").GetString()!;
                var answer = Bad ? "Rina pulang. Dia makan." : LastPrompt.Contains("Earlier source passage", StringComparison.Ordinal)
                    ? "Rina wohnt in der Stadt.\n\nRina ging nach Hause." : "Rina ging nach Hause.";
                var body = JsonSerializer.Serialize(new { response = answer, eval_count = 20, eval_duration = 2000000000 });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }

        [TestMethod]
        public void QualityGate_RejectsEchoedOrMostlyUntranslatedJapanese()
        {
            var source = "私は三十四歳で、仕事がなく、家もありません。";
            Assert.IsTrue(OllamaLocalModels.LooksComplete(source, "Ich bin 34 Jahre alt, arbeitslos und obdachlos.", "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, source, "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, "私は三十四歳で仕事も家もありません。", "de"));
            Assert.IsFalse(OllamaLocalModels.LooksComplete(source, "x", "de"));
        }

        [TestMethod]
        public void ProviderPaths_KeepModelsAndTargetsSeparate()
        {
            var first = LocalReadingPaths.Cache(12, "de", "ollama:translategemma:4b");
            var second = LocalReadingPaths.Cache(12, "de", "ollama:translategemma:12b");
            Assert.AreNotEqual(first, second);
            Assert.AreNotEqual(first, LocalReadingPaths.Cache(12, "id", "ollama:translategemma:4b"));
            Assert.IsFalse(first.StartsWith(":translation/", StringComparison.Ordinal), "Removed Mozilla rows used that prefix; migration 0041 deletes it.");

            // A cache key names its model id again.
            Assert.AreEqual("ollama:translategemma:4b", LocalReadingPaths.ModelId(LocalReadingPaths.ModelKey("ollama:translategemma:4b")));
            Assert.AreEqual("codex:gpt-5", LocalReadingPaths.ModelId(LocalReadingPaths.ModelKey("codex:gpt-5")));

            // Ollama keeps the cache keys of before provider-qualified ids, so nothing is translated again; another provider's key is its whole id.
            Assert.AreEqual(":localtranslation/v4/translategemma:4b/de/00012", first);
            Assert.AreEqual(":localtranslation/v4/codex:gpt-5/de/100003", LocalReadingPaths.Cache(100003, "de", "codex:gpt-5"));
        }

        [TestMethod]
        public void Providers_ResolveQualifiedModelIds_AndOnlyApprovedModels()
        {
            using var http = new HttpClient(new Machines());
            var machines = new LocalTranslationMachines(new OllamaLocalModels(http), Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434" }), new Clock());
            var providers = new TranslationProviders([new OllamaTranslationProvider(new OllamaLocalModels(http), machines)]);

            Assert.AreEqual(("ollama", "translategemma:12b"), providers.Find("ollama:translategemma:12b") is var (provider, model) ? (provider.Key, model) : default);
            foreach (var id in new[] { "translategemma:12b", "ollama:untrusted:90b", "codex:gpt-5", ":translategemma:4b", "ollama:", "", null })
                Assert.IsNull(providers.Find(id), id);
            Assert.AreEqual("ollama:translategemma:4b", TranslationProviders.Id(OllamaTranslationProvider.ProviderKey, "translategemma:4b"));
        }

        [TestMethod]
        public async Task Providers_TheOwnersDefaultModel_WinsAndIsNeverReplacedByAnother()
        {
            using var http = new HttpClient(new Machines());
            var machines = new LocalTranslationMachines(new OllamaLocalModels(http), Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434" }), new Clock());
            var providers = new TranslationProviders([new OllamaTranslationProvider(new OllamaLocalModels(http), machines)]);
            string[] order = ["http://192.168.1.20:11434", "http://10.0.0.2:11434"];

            // Without a default: the largest model of the first machine that answers, as before.
            Assert.AreEqual("ollama:translategemma:4b", (await providers.ChooseAsync(new TranslationSetupV1(order, null), default))?.ModelId);
            // The default goes to the machine that has it; a named model wins over the default.
            var chosen = await providers.ChooseAsync(new TranslationSetupV1(order, "ollama:translategemma:12b"), default);
            Assert.AreEqual(("ollama:translategemma:12b", new Uri("http://10.0.0.2:11434/")), (chosen?.ModelId, chosen?.Address));
            Assert.AreEqual("ollama:translategemma:4b", (await providers.ChooseAsync(new TranslationSetupV1(order, "ollama:translategemma:12b"), default, "ollama:translategemma:4b"))?.ModelId);
            // A default no machine has, or an unknown one, translates nothing rather than with another model.
            Assert.IsNull(await providers.ChooseAsync(new TranslationSetupV1(order, "ollama:translategemma:27b"), default));
            Assert.IsNull(await providers.ChooseAsync(new TranslationSetupV1(order, "codex:gpt-5"), default));
        }

        [TestMethod]
        public void Cache_RejectsStaleSourceLanguageAndAlgorithm_AndKeepsAnEarlierModelVersionAsStale()
        {
            const string html = "<p data-b=\"0\">Rina pulang.</p>";
            var digest = new string('a', 64);
            var source = LocalReadingPaths.SourceHash(html, "id");
            var cache = new LocalTranslationCache(LocalReadingPaths.Algorithm, "id", source, digest, ["Rina ging nach Hause.", null]);
            var json = JsonSerializer.Serialize(cache);
            Assert.IsNotNull(LocalReadingPaths.Parse(json, html, "id", digest, 2), "A partial cache must survive refresh.");
            Assert.IsNull(LocalReadingPaths.Parse(json, html, "ja", digest, 2));
            Assert.IsNull(LocalReadingPaths.Parse(json, html + "changed", "id", digest, 2));
            Assert.IsNull(LocalReadingPaths.Parse(json, html, "id", digest, 1));
            Assert.IsNull(LocalReadingPaths.Parse("{broken", html, "id", digest, 2));

            // A cache written before stale texts existed reads as before.
            var earlier = $$"""{"AlgorithmVersion":4,"SourceLanguage":"id","SourceHash":"{{source}}","ModelDigest":"{{digest}}","Texts":["Rina ging nach Hause.",null]}""";
            Assert.AreEqual("Rina ging nach Hause.", LocalReadingPaths.Parse(earlier, html, "id", digest, 2)!.Texts[0]);

            // A new version of the model starts empty and keeps the earlier text visible as stale; read as stored, the cache is unchanged.
            var renewed = LocalReadingPaths.Parse(json, html, "id", new string('b', 64), 2)!;
            Assert.AreEqual(new string('b', 64), renewed.ModelDigest);
            CollectionAssert.AreEqual(new string?[] { null, null }, renewed.Texts);
            Assert.AreEqual(("Rina ging nach Hause.", true), LocalReadingPaths.Visible(renewed, 0));
            Assert.AreEqual(((string?)null, false), LocalReadingPaths.Visible(renewed, 1));
            Assert.AreEqual(("Rina ging nach Hause.", false), LocalReadingPaths.Visible(LocalReadingPaths.Parse(json, html, "id", null, 2)!, 0));
        }

        [TestMethod]
        public void Chunker_LongParagraph_KeepsOrderedTextAndBoundedUnits()
        {
            var text = string.Join(' ', Enumerable.Repeat("Rina ging nach Hause.", 300));
            var chunks = OllamaLocalModels.Segments(text);
            Assert.IsTrue(chunks.Count > 1);
            Assert.IsTrue(chunks.All(chunk => chunk.Length is > 0 and <= 2400));
            Assert.AreEqual(text, string.Join(' ', chunks));
            Assert.ThrowsExactly<ArgumentException>(() => OllamaLocalModels.Segments(new string('a', 20001)));
        }

        [TestMethod]
        public async Task Lanes_EachProviderHasItsOwn_APersonGoesBeforeTheBookJob_AndAWaitEnds()
        {
            var time = new WorkerJobTests.ManualTime(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
            var lanes = new TranslationLanes(time);

            // Codex translates while Ollama does; another Ollama translation waits for the one lane of every Ollama machine.
            var ollama = await lanes.TryEnterAsync("ollama", TimeSpan.Zero, default);
            Assert.IsNotNull(ollama);
            using (var codex = await lanes.TryEnterAsync("codex", TimeSpan.Zero, default)) Assert.IsNotNull(codex);
            Assert.IsNull(await lanes.TryEnterAsync("ollama", TimeSpan.Zero, default));

            // The book job asked first and the Reader after it: the Reader gets the lane, and the job's short wait ends while the Reader translates.
            var job = lanes.TryEnterAsync("ollama", TimeSpan.FromSeconds(2), default, background: true);
            var reader = lanes.TryEnterAsync("ollama", TimeSpan.FromSeconds(20), default);
            ollama.Dispose();
            var readerHold = await reader;
            Assert.IsNotNull(readerHold);
            Assert.IsFalse(job.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.IsNull(await job);

            // A job that waits gets the lane once nobody else does; a cancelled wait throws and leaves the line.
            var next = lanes.TryEnterAsync("ollama", TimeSpan.FromSeconds(2), default, background: true);
            using var cancel = new CancellationTokenSource();
            var cancelled = lanes.TryEnterAsync("ollama", TimeSpan.FromSeconds(20), cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cancelled);
            readerHold.Dispose();
            readerHold.Dispose();
            var jobHold = await next;
            Assert.IsNotNull(jobHold);
            jobHold.Dispose();
            using var free = await lanes.TryEnterAsync("ollama", TimeSpan.Zero, default);
            Assert.IsNotNull(free, "Every hold handed the lane on once.");
        }

        [TestMethod]
        public void DownloadQueue_DeniesModelsWithoutOperatorVerifiedVolumeBudget()
        {
            using var http = new HttpClient(new FakeOllama());
            var client = new OllamaLocalModels(http);
            var root = Path.Combine(Path.GetTempPath(), "jularr-local-pull-" + Guid.NewGuid().ToString("N"));
            using var noCapacity = new LocalTranslationDownloads(client,
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 0 }),
                Options.Create(new MediaOptions { TranslationPath = Path.Combine(root, "denied") }));
            Assert.IsFalse(noCapacity.Queue("translategemma:4b", Ollama, 0));
            Assert.IsFalse(noCapacity.Queue("other:27b", Ollama, 0));

            using var adequate = new LocalTranslationDownloads(client,
                Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L }),
                Options.Create(new MediaOptions { TranslationPath = Path.Combine(root, "approved") }));
            Assert.IsTrue(adequate.Queue("translategemma:4b", Ollama, 6_000_000_000L));
            Assert.IsFalse(adequate.Queue("translategemma:12b", Ollama, 6_000_000_000L));
            Assert.AreEqual("queued", adequate.Current().State);
        }

        [TestMethod]
        public void DownloadStatus_SurvivesRestart_AsInterruptedUntilExplicitRetry()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-pull-restart-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var http = new HttpClient(new FakeOllama());
                var client = new OllamaLocalModels(http);
                var settings = Options.Create(new LocalTranslationOptions
                {
                    Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L
                });
                var media = Options.Create(new MediaOptions { TranslationPath = root });
                using (var beforeRestart = new LocalTranslationDownloads(client, settings, media))
                {
                    Assert.IsTrue(beforeRestart.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                    Assert.AreEqual("queued", beforeRestart.Current().State);
                    Assert.IsTrue(File.Exists(Path.Combine(root, "ollama-pull-state.json")));
                }
                // No automatic network activity: restart reports interruption and the owner
                // explicitly retries the same model, whose partial blobs Ollama retains.
                using (var afterRestart = new LocalTranslationDownloads(client, settings, media))
                {
                    Assert.AreEqual("interrupted", afterRestart.Current().State);
                    Assert.AreEqual("translategemma:4b", afterRestart.Current().Model);
                    Assert.IsTrue(afterRestart.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                    Assert.AreEqual("queued", afterRestart.Current().State);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task Download_ThatStopsSendingData_FailsAfterItsIdleLimit_AndIsStartedAgainOnlyByHand()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-pull-stall-" + Guid.NewGuid().ToString("N"));
            try
            {
                var server = SilentServer.StallsAfter("""{"status":"pulling manifest"}""", """{"status":"pulling","total":100,"completed":40}""");
                using var http = new HttpClient(server);
                var limit = TimeSpan.FromMilliseconds(300);
                using var downloads = new LocalTranslationDownloads(new OllamaLocalModels(http) { PullIdleLimit = limit },
                    Options.Create(new LocalTranslationOptions { Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L }),
                    Options.Create(new MediaOptions { TranslationPath = root }));
                await downloads.StartAsync(default);

                // The machine sends some progress and then nothing: the download fails once the limit passed without data.
                var started = Stopwatch.StartNew();
                Assert.IsTrue(downloads.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                while (downloads.Current().State != "failed" && started.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                Assert.AreEqual(("failed", 40), (downloads.Current().State, downloads.Current().Progress));
                Assert.IsTrue(started.Elapsed >= limit, started.Elapsed.ToString());

                // Nothing starts it again by itself; the Owner does, and stopping the host interrupts it rather than failing it.
                Assert.AreEqual(1, server.Requests);
                Assert.IsTrue(downloads.Queue("translategemma:4b", Ollama, 6_000_000_000L));
                while (server.Requests < 2 && started.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
                await downloads.StopAsync(default);
                Assert.AreEqual("interrupted", downloads.Current().State);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void DownloadStatus_RejectsCorruptPersistentJob_FailsClosed()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-pull-corrupt-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "ollama-pull-state.json"), "{invalid-json");
                using var http = new HttpClient(new FakeOllama());
                using var downloads = new LocalTranslationDownloads(new OllamaLocalModels(http),
                    Options.Create(new LocalTranslationOptions
                    {
                        Endpoint = "http://ollama:11434", OperatorVerifiedFreeBytes = 6_000_000_000L
                    }),
                    Options.Create(new MediaOptions { TranslationPath = root }));
                Assert.AreEqual("failed", downloads.Current().State);
                Assert.IsFalse(downloads.Queue("translategemma:4b", Ollama, 6_000_000_000L));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task FreeSpace_TypedByTheOwner_IsReadBackAndANegativeValueIsRefused()
        {
            await using var host = await LibraryTests.CreateHostAsync(Path.Combine(Path.GetTempPath(), "jularr-free-" + Guid.NewGuid().ToString("N")), razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            using var save = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/free-bytes", new TranslationFreeBytesDataV1(50_000_000_000L));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, save.StatusCode, await save.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/models");
            using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.AreEqual(50_000_000_000L, json.RootElement.GetProperty("verifiedFreeBytes").GetInt64());

            using var negative = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/free-bytes", new TranslationFreeBytesDataV1(-1));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, negative.StatusCode);
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public string? LastPrompt { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/tags")
                    body = """{"models":[{"name":"translategemma:4b","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"name":"arbitrary:999b"}]}""";
                else if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/ps")
                    body = """{"models":[{"name":"translategemma:4b"}]}""";
                else if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/generate")
                {
                    var source = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using var json = JsonDocument.Parse(source);
                    Assert.AreEqual("translategemma:4b", json.RootElement.GetProperty("model").GetString());
                    Assert.IsFalse(json.RootElement.GetProperty("stream").GetBoolean());
                    var prompt = json.RootElement.GetProperty("prompt").GetString()!;
                    LastPrompt = prompt;
                    StringAssert.Contains(prompt, "Indonesian (id) to German (de) translator");
                    StringAssert.Contains(prompt, "into German:\n\n\nRina pulang.");
                    body = """{"response":"Rina ging nach Hause.","eval_count":20,"eval_duration":2000000000}""";
                }
                else throw new InvalidOperationException(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
