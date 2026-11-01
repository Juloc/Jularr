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

        protected override void Define(Page page)
        {
            page.Title("translation.local.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Translation/Translation.entry.ts");
            page.Content.Add(new P(new Text("translation.local.description")));
            page.Content.Add(new A(new Text("translation.local.back")).Attribute("href", "/Admin/Instance"));
            if (_models is null) return;
            if (!_models.Configured)
                page.Content.Add(new P(new Text("translation.local.configure")).Attribute("role", "status"));
            else if (_models.Problem is not null)
                page.Content.Add(new P(new Text("translation.local.offline")).Attribute("role", "alert"));

            page.Content.Add(new P(new ValueText(texts["translation.local.capacity"] + " "
                + Math.Round(_models.VerifiedFreeBytes / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB")));
            // The free space of Ollama's model volume, which only the Owner can know; model downloads are offered up to it.
            var csrfFree = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new Form<TranslationFreeBytesDataV1>(form =>
            {
                form.ByteSize(item => item.FreeBytes, "translation.local.freeBytes", new FieldHelp(Info: "translation.local.freeBytesHint"));
                form.Save();
            }, Home + "?handler=FreeBytes", csrfFree, _freeBytes ?? new TranslationFreeBytesDataV1(_models.VerifiedFreeBytes), _freeBytes is null ? null : FormError));
            // The queue belongs to Jularr and remains visible even when Ollama is offline.
            // Polling updates only this status; a finished download reloads the model actions.
            var download = new P(new ValueText(_models.DownloadState is "idle" ? ""
                : texts["translation.local.state." + _models.DownloadState] + ": " + _models.DownloadModel
                    + " (" + _models.DownloadProgress.ToString(CultureInfo.InvariantCulture) + "%)"))
                .Attribute("data-translation-download", "")
                .Attribute("data-models-url", "/api/v1/admin/translation/models")
                .Attribute("data-download-state", _models.DownloadState)
                .Attribute("role", "status")
                .Attribute("aria-live", "polite");
            foreach (var state in new[] { "queued", "downloading", "installed", "failed", "interrupted" })
                download.Attribute("data-state-" + state, texts["translation.local.state." + state]);
            if (_models.DownloadState is "idle") download.Attribute("hidden", "hidden");
            page.Content.Add(download);
            if (_models.DownloadState is "interrupted")
                page.Content.Add(new P(new Text("translation.local.retryInfo")).Attribute("role", "status"));
            if (_models.DownloadState is "failed")
                page.Content.Add(new P(new Text("translation.local.failureInfo")).Attribute("role", "alert"));

            // What the job "Book translations" does now: it translates every book into every language a profile uses as soon as a machine answers with a model.
            var preparing = !_models.Configured ? "noMachine"
                : _models.Machines.All(machine => !machine.Reachable) ? "unreachable"
                : _models.Machines.Any(machine => machine.Reachable && machine.Installed.Any(_models.Approved.Contains)) ? "ready" : "noModel";
            page.Content.Add(new Div(new P(new Text("translation.local.prepare." + preparing)),
                new A(new Text("translation.local.prepare.jobs")).Attribute("href", "/Admin/Jobs")).Class("feature-card")
                .Attribute("data-translation-prepare", preparing).Attribute("role", "status"));

            // How far the job is; the page reads this part again every ten seconds.
            if (_progress is not null) page.Content.Add(TranslationProgressView.Full(_progress, texts));

            // What the browser of the Owner kept of its own Mozilla translations (a week, at most 300 MB): only this browser can empty it.
            if (texts.CanTranslateInBrowser())
                page.Content.Add(new Div(
                    new Button(new Text("translation.browserCache.clear")).Attribute("type", "button").Attribute("data-translation-clear-browser-cache", ""),
                    new P(new Text("translation.browserCache.cleared")).Attribute("data-translation-browser-cache-cleared", "").Attribute("role", "status").Attribute("hidden", "hidden")
                ).Class("feature-card"));

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            // The Ollama machines in order: the first that answers with a model translates, the next takes over when it drops out.
            page.Content.Add(new Form<TranslationMachinesDataV1>(form =>
            {
                form.Multiline(item => item.Machines, "translation.local.machines");
                form.Save();
            }, Home + "?handler=Machines", csrf, _machines ?? new TranslationMachinesDataV1([.. _models.Machines.Select(machine => machine.Address)]),
                _machines is null ? null : FormError));
            var translating = Array.FindIndex(_models.Machines, machine => machine.Reachable && machine.Installed.Any(_models.Approved.Contains));
            for (var place = 0; place < _models.Machines.Length; place++)
            {
                var machine = _models.Machines[place];
                var state = !machine.Reachable ? texts["translation.local.machine.offline"]
                    : place == translating ? texts["translation.local.machine.translating"] : texts["translation.local.machine.waiting"];
                var models = machine.Installed.Where(_models.Approved.Contains).ToArray();
                var parts = new List<IUiComponent> { new P(new ValueText((place + 1).ToString(CultureInfo.InvariantCulture) + ". " + machine.Address + " · " + state
                    + (models.Length > 0 ? " · " + string.Join(", ", models) : ""))) };
                foreach (var model in _models.Approved.Where(model => machine.Reachable && !machine.Installed.Contains(model, StringComparer.Ordinal)))
                {
                    var size = model switch { "translategemma:4b" => 3_300_000_000L, "translategemma:12b" => 8_100_000_000L, _ => 17_000_000_000L };
                    if (_models.VerifiedFreeBytes < size + 2_000_000_000L || _models.DownloadState is "queued" or "downloading") continue;
                    var retry = _models.DownloadModel == model && _models.DownloadMachine == machine.Address && _models.DownloadState is "interrupted" or "failed";
                    parts.Add(new PostForm(Home + "?handler=Pull&model=" + Uri.EscapeDataString(model) + "&machine=" + place.ToString(CultureInfo.InvariantCulture), csrf, [],
                        new Button(new ValueText(texts[retry ? "translation.local.retry" : "translation.local.pull"] + " · " + model + " · "
                            + Math.Round(size / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB")).Submit()).Attribute("data-translation-submit", "pull"));
                }
                page.Content.Add(new Div([.. parts]).Class("feature-card").Attribute("data-translation-machine", machine.Address));
            }

            // The approved models as the machine that translates now has them; they stay listed while no machine answers.
            foreach (var model in _models.Approved)
            {
                var installed = _models.Installed.Contains(model, StringComparer.Ordinal);
                var state = installed ? _models.Running.Contains(model, StringComparer.Ordinal) ? "translation.local.loaded" : "translation.local.installed" : "translation.local.notInstalled";
                page.Content.Add(new Div(
                    new P(new ValueText(model + " · " + texts[state])),
                    installed && _models.Problem is null
                        ? new PostForm(Home + "?handler=Benchmark&model=" + Uri.EscapeDataString(model), csrf, [],
                            new Button(new Text("translation.local.benchmark")).Submit()).Attribute("data-translation-submit", "benchmark")
                        : new Span()
                ).Class("feature-card"));
            }

            page.Content.Add(new A(new Text("translation.local.refresh")).Attribute("href", Home));
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

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
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
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostFreeBytesAsync(long? freeBytes)
        {
            _freeBytes = new TranslationFreeBytesDataV1(freeBytes ?? 0);
            await saveFreeBytes.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), _freeBytes, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMachinesAsync(string? machines)
        {
            _machines = new TranslationMachinesDataV1((machines ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            await saveMachines.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), _machines, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostBenchmarkAsync(string model)
        {
            _benchmark = await benchmark.ExecuteAsync<LocalTranslationBenchmarkV1>(new LocalTranslationModelParametersV1(model), new NoData(), HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _models = await read.ExecuteAsync<LocalTranslationModelsV1>(cancellationToken: HttpContext.RequestAborted);
            _progress = await readProgress.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: HttpContext.RequestAborted);
        }
    }
}
