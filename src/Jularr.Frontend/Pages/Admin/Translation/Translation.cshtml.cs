using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Admin.Translation.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Translation
{
    public sealed class TranslationModelPage(ReadLocalTranslationModelsV1 read, ReadBookTranslationProgressV1 readProgress, PullLocalTranslationModelV1 pull,
        BenchmarkLocalTranslationModelV1 benchmark, SaveTranslationMachinesV1 saveMachines, SaveTranslationFreeBytesV1 saveFreeBytes, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Home = "/Admin/Instance/Translation";
        private LocalTranslationModelsV1? _models;
        private BookTranslationProgressV1? _progress;
        private LocalTranslationBenchmarkV1? _benchmark;
        private TranslationMachinesDataV1? _machines;
        private TranslationFreeBytesDataV1? _freeBytes;

        // Two tabs: how far the translation is, and what it needs (computers, models, space).
        [BindProperty(SupportsGet = true, Name = "tab")]
        public string? Tab { get; set; }

        private bool Setup => string.Equals(Tab, "setup", StringComparison.OrdinalIgnoreCase);
        private const string SetupAddress = Home + "?tab=setup";

        protected override void Define(Page page)
        {
            page.Title("translation.local.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Translation/Translation.entry.scss");
            page.Asset("Pages/Admin/Translation/Translation.entry.ts");
            page.Content.Add(new P(new Text("translation.local.description")));
            if (_models is null) return;
            page.Content.Add(new NavigationTabs([new NavigationTab("translation.tab.progress", Home, !Setup), new NavigationTab("translation.tab.setup", SetupAddress, Setup)], "translation.local.title"));
            if (Setup) DefineSetup(page, _models);
            else DefineProgress(page, _models);
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // Whether the job can work now, then how far it is; the page reads the progress again every ten seconds.
        private void DefineProgress(Page page, LocalTranslationModelsV1 models)
        {
            var preparing = !models.Configured ? "noMachine"
                : models.Machines.All(machine => !machine.Reachable) ? "unreachable"
                : models.Machines.Any(machine => machine.Reachable && machine.Installed.Any(models.Approved.Contains)) ? "ready" : "noModel";
            page.Content.Add(new Div(new P(new Text("translation.local.prepare." + preparing)),
                new Span(new A(new Text("translation.local.prepare.jobs")).Attribute("href", "/Admin/Jobs"), new ValueText(" · "),
                    new A(new Text(preparing == "ready" ? "translation.tab.setup" : "translation.local.prepare.fix")).Attribute("href", SetupAddress))).Class("feature-card")
                .Attribute("data-translation-prepare", preparing).Attribute("role", "status"));
            if (_progress is not null) page.Content.Add(TranslationProgressView.Full(_progress, texts));
        }

        // The computers in their order with their models, the free space, the speed test and the browser's own cache.
        private void DefineSetup(Page page, LocalTranslationModelsV1 models)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (!models.Configured)
                page.Content.Add(new P(new Text("translation.local.configure")).Attribute("role", "status"));
            else if (models.Problem is not null)
                page.Content.Add(new P(new Text("translation.local.offline")).Attribute("role", "alert"));

            // The Ollama machines in order: the first that answers with a model translates, the next takes over when it drops out.
            page.Content.Add(new P(new Text("translation.setup.machinesHelp")));
            page.Content.Add(new Form<TranslationMachinesDataV1>(form =>
            {
                form.Multiline(item => item.Machines, "translation.local.machines");
                form.Save();
            }, Home + "?handler=Machines", csrf, _machines ?? new TranslationMachinesDataV1([.. models.Machines.Select(machine => machine.Address)]),
                _machines is null ? null : FormError));
            var translating = Array.FindIndex(models.Machines, machine => machine.Reachable && machine.Installed.Any(models.Approved.Contains));
            for (var place = 0; place < models.Machines.Length; place++)
            {
                var machine = models.Machines[place];
                var state = !machine.Reachable ? texts["translation.local.machine.offline"]
                    : place == translating ? texts["translation.local.machine.translating"] : texts["translation.local.machine.waiting"];
                var installed = machine.Installed.Where(models.Approved.Contains).ToArray();
                var parts = new List<IUiComponent> { new P(new ValueText((place + 1).ToString(CultureInfo.InvariantCulture) + ". " + machine.Address + " · " + state
                    + (installed.Length > 0 ? " · " + string.Join(", ", installed) : ""))) };
                foreach (var model in models.Approved.Where(model => machine.Reachable && !machine.Installed.Contains(model, StringComparer.Ordinal)))
                {
                    var size = model switch { "translategemma:4b" => 3_300_000_000L, "translategemma:12b" => 8_100_000_000L, _ => 17_000_000_000L };
                    if (models.VerifiedFreeBytes < size + 2_000_000_000L || models.DownloadState is "queued" or "downloading") continue;
                    var retry = models.DownloadModel == model && models.DownloadMachine == machine.Address && models.DownloadState is "interrupted" or "failed";
                    parts.Add(new PostForm(Home + "?handler=Pull&model=" + Uri.EscapeDataString(model) + "&machine=" + place.ToString(CultureInfo.InvariantCulture), csrf, [],
                        new Button(new ValueText(texts[retry ? "translation.local.retry" : "translation.local.pull"] + " · " + model + " · "
                            + Math.Round(size / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB · " + machine.Address)).Submit()).Attribute("data-translation-submit", "pull"));
                }
                page.Content.Add(new Div([.. parts]).Class("feature-card").Attribute("data-translation-machine", machine.Address));
            }

            // The queue belongs to Jularr and remains visible even when Ollama is offline.
            // Polling updates only this status; a finished download reloads the model actions.
            var download = new P(new ValueText(models.DownloadState is "idle" ? ""
                : texts["translation.local.state." + models.DownloadState] + ": " + models.DownloadModel
                    + " (" + models.DownloadProgress.ToString(CultureInfo.InvariantCulture) + "%)"))
                .Attribute("data-translation-download", "")
                .Attribute("data-models-url", "/api/v1/admin/translation/models")
                .Attribute("data-download-state", models.DownloadState)
                .Attribute("role", "status")
                .Attribute("aria-live", "polite");
            foreach (var state in new[] { "queued", "downloading", "installed", "failed", "interrupted" })
                download.Attribute("data-state-" + state, texts["translation.local.state." + state]);
            if (models.DownloadState is "idle") download.Attribute("hidden", "hidden");
            page.Content.Add(download);
            if (models.DownloadState is "interrupted")
                page.Content.Add(new P(new Text("translation.local.retryInfo")).Attribute("role", "status"));
            if (models.DownloadState is "failed")
                page.Content.Add(new P(new Text("translation.local.failureInfo")).Attribute("role", "alert"));

            // The free space of Ollama's model volume, which only the Owner can know; model downloads are offered up to it.
            page.Content.Add(new P(new ValueText(texts["translation.local.capacity"] + " "
                + Math.Round(models.VerifiedFreeBytes / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB")));
            page.Content.Add(new Form<TranslationFreeBytesDataV1>(form =>
            {
                form.ByteSize(item => item.FreeBytes, "translation.local.freeBytes", new FieldHelp(Info: "translation.local.freeBytesHint"));
                form.Save();
            }, Home + "?handler=FreeBytes", csrf, _freeBytes ?? new TranslationFreeBytesDataV1(models.VerifiedFreeBytes), _freeBytes is null ? null : FormError));

            // The approved models as the machine that translates now has them; they stay listed while no machine answers.
            foreach (var model in models.Approved)
            {
                var installed = models.Installed.Contains(model, StringComparer.Ordinal);
                var state = installed ? models.Running.Contains(model, StringComparer.Ordinal) ? "translation.local.loaded" : "translation.local.installed" : "translation.local.notInstalled";
                page.Content.Add(new Div(
                    new P(new ValueText(model + " · " + texts[state])),
                    installed && models.Problem is null
                        ? new Span(new PostForm(Home + "?handler=Benchmark&model=" + Uri.EscapeDataString(model), csrf, [],
                            new Button(new Text("translation.local.benchmark")).Submit()).Attribute("data-translation-submit", "benchmark"), new Span(new Text("translation.local.benchmarkHint")).Class("list-value list-value--dim"))
                        : new Span()
                ).Class("feature-card"));
            }

            if (_benchmark is { } result)
            {
                page.Content.Add(new P(new Text("translation.local.benchmarkWarning")));
                for (var index = 0; index < result.Samples.Length; index++)
                {
                    page.Content.Add(new P(new ValueText(result.Samples[index] + " — "
                        + result.Seconds[index].ToString("0.0", CultureInfo.InvariantCulture) + " s; "
                        + result.TokensPerSecond[index].ToString("0.0", CultureInfo.InvariantCulture) + " tokens/s")));
                }
            }

            // What the browser of the Owner kept of its own Mozilla translations (a week, at most 300 MB): only this browser can empty it.
            if (texts.CanTranslateInBrowser())
                page.Content.Add(new Div(
                    new Button(new Text("translation.browserCache.clear")).Attribute("type", "button").Attribute("data-translation-clear-browser-cache", ""),
                    new P(new Text("translation.browserCache.cleared")).Attribute("data-translation-browser-cache-cleared", "").Attribute("role", "status").Attribute("hidden", "hidden")
                ).Class("feature-card"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Only the progress part, for the page to read again while it is open.
        public async Task<IActionResult> OnGetProgressAsync()
        {
            var progress = await readProgress.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: HttpContext.RequestAborted);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            TranslationProgressView.Full(progress, texts).WriteTo(writer, global::System.Text.Encodings.Web.HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        public async Task<IActionResult> OnPostPullAsync(string model, int machine)
        {
            await pull.ExecuteAsync<UpdateResult>(new LocalTranslationPullParametersV1(model, machine), new NoData(), HttpContext.RequestAborted);
            return LocalRedirect(SetupAddress);
        }

        public async Task<IActionResult> OnPostFreeBytesAsync(long? freeBytes)
        {
            _freeBytes = new TranslationFreeBytesDataV1(freeBytes ?? 0);
            Tab = "setup";
            await saveFreeBytes.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), _freeBytes, HttpContext.RequestAborted);
            return LocalRedirect(SetupAddress);
        }

        public async Task<IActionResult> OnPostMachinesAsync(string? machines)
        {
            _machines = new TranslationMachinesDataV1((machines ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            Tab = "setup";
            await saveMachines.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), _machines, HttpContext.RequestAborted);
            return LocalRedirect(SetupAddress);
        }

        public async Task<IActionResult> OnPostBenchmarkAsync(string model)
        {
            Tab = "setup";
            _benchmark = await benchmark.ExecuteAsync<LocalTranslationBenchmarkV1>(new LocalTranslationModelParametersV1(model), new NoData(), HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _models = await read.ExecuteAsync<LocalTranslationModelsV1>(cancellationToken: HttpContext.RequestAborted);
            if (!Setup) _progress = await readProgress.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: HttpContext.RequestAborted);
        }
    }
}
