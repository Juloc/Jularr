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
    public sealed class TranslationModelPage(ReadLocalTranslationModelsV1 read, ReadBookTranslationProgressV1 readProgress, ReadTranslatedBooksV1 readBooks, PullLocalTranslationModelV1 pull,
        BenchmarkLocalTranslationModelV1 benchmark, SaveTranslationMachinesV1 saveMachines, SaveTranslationFreeBytesV1 saveFreeBytes, SaveTranslationModelV1 saveModel,
        CheckTranslationMachinesV1 check, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private const string Home = "/Admin/Instance/Translation";
        private LocalTranslationModelsV1? _models;
        private BookTranslationProgressV1? _progress;
        private PageResult<TranslatedBookV1>? _books;
        private LocalTranslationBenchmarkV1? _benchmark;
        private TranslationMachinesDataV1? _machines;
        private TranslationFreeBytesDataV1? _freeBytes;
        private TranslationModelDataV1? _model;

        // Three tabs: how far the translation is, the translated works with their EPUBs, and what it needs (computers, models, space).
        [BindProperty(SupportsGet = true, Name = "tab")]
        public string? Tab { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        private bool Setup => string.Equals(Tab, "setup", StringComparison.OrdinalIgnoreCase);
        private bool Books => string.Equals(Tab, "books", StringComparison.OrdinalIgnoreCase);
        private const string SetupAddress = Home + "?tab=setup";
        private const string BooksAddress = Home + "?tab=books";

        protected override void Define(Page page)
        {
            page.Title("translation.local.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Translation/Translation.entry.scss");
            page.Asset("Pages/Admin/Translation/Translation.entry.ts");
            page.Content.Add(new P(new Text("translation.local.description")));
            if (_models is null) return;
            page.Content.Add(new NavigationTabs([new NavigationTab("translation.tab.progress", Home, !Setup && !Books), new NavigationTab("translation.tab.books", BooksAddress, Books),
                new NavigationTab("translation.tab.setup", SetupAddress, Setup)], "translation.local.title"));
            if (Setup) DefineSetup(page, _models);
            else if (Books && _books is { } books) DefineBooks(page, books);
            else DefineProgress(page, _models);
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // Whether the job can work now (one status card), then how far it is; the page reads the progress again every ten seconds.
        private void DefineProgress(Page page, LocalTranslationModelsV1 models)
        {
            var preparing = !models.Configured ? "noMachine"
                : models.Machines.All(machine => !machine.Reachable) ? "unreachable"
                : models.Machines.Any(machine => machine.Reachable && machine.Installed.Any(models.Approved.Contains)) ? "ready" : "noModel";
            var tone = preparing == "ready" ? UiTone.Success : preparing == "unreachable" ? UiTone.Danger : UiTone.Warning;
            var status = new Div(
                new Div(new StateChip(new Text("translation.status." + preparing), tone, new Icon(preparing == "ready" ? "check" : "alert", assets, 14)),
                    new P(new Text("translation.local.prepare." + preparing))).Class("translation-status__line"),
                new Div(new A(new Text("translation.local.prepare.jobs")).Class("button button--soft").Attribute("href", "/Admin/Jobs"),
                    new A(new Text(preparing == "ready" ? "translation.tab.setup" : "translation.local.prepare.fix")).Class("button button--soft").Attribute("href", SetupAddress)).Class("page-actions"))
                .Class("translation-status").Attribute("data-translation-prepare", preparing).Attribute("role", "status");
            page.Content.Add(new UiSection("translation.status.title", [status], new Icon("status", assets, 18)));
            if (_progress is not null) page.Content.Add(TranslationProgressView.Full(_progress, texts));
        }

        // The works the server translated, one row per book and language: each model with its sections, the sections still in an earlier version
        // of the model, and the translated EPUB once one model covers every section.
        private void DefineBooks(Page page, PageResult<TranslatedBookV1> books)
        {
            page.Content.Add(new P(new Text("translation.books.description")));
            var columns = new List<ListColumn> { new("translation.books.work", ListRole.Main), new("translation.books.language", ListRole.Status), new("translation.books.models", ListRole.Data),
                new("common.actions", ListRole.Actions) };
            var empty = new Div(new IconTile(new Icon("book", assets, 22), UiTone.Neutral), new P(new Text("translation.books.empty")));
            page.Content.Add(new ListPanel("translation.books.title", columns, [.. books.Items.Select(BookRow)], empty).Modifier("translation-books").Paged());
        }

        private ListRow BookRow(TranslatedBookV1 book)
        {
            var title = new A(new Truncated(book.Title)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + book.WorkId.ToString(CultureInfo.InvariantCulture));
            var sections = string.Format(CultureInfo.CurrentCulture, texts["translation.books.sections"], book.Translated.ToString("N0", CultureInfo.CurrentCulture), book.Sections.ToString("N0", CultureInfo.CurrentCulture));
            var line = book.Volume is { } volume ? texts["translation.progress.volume"] + " " + volume + " · " + sections : sections;
            var identity = new ListIdentity(new IconTile(new Icon("book", assets, 20), book.IsComplete() ? UiTone.Success : UiTone.Accent), title, new ValueText(line));
            var language = new StateChip(new ValueText(LanguageName(book.Language)), book.IsComplete() ? UiTone.Success : UiTone.Info, new Icon(book.IsComplete() ? "check" : "globe", assets, 14));

            // Each model on its own line: its sections, and those an earlier version of it still shows.
            var models = new Div([.. book.Models.Select((model, index) => (IUiComponent)new Span(new ValueText(model + " · "
                + string.Format(CultureInfo.CurrentCulture, texts["translation.books.sections"], book.Done[index].ToString("N0", CultureInfo.CurrentCulture), book.Sections.ToString("N0", CultureInfo.CurrentCulture))
                + (book.Stale[index] > 0 ? " · " + string.Format(CultureInfo.CurrentCulture, texts["translation.books.stale"], book.Stale[index].ToString("N0", CultureInfo.CurrentCulture)) : ""))).Class("list-value"))])
                .Class("translation-books__models");
            IUiComponent download = book.IsComplete()
                ? new A(new Icon("download", assets, 16), new Span(new Text("translation.books.download"))).Class("button")
                    .Attribute("href", "/api/v1/reading/documents/" + book.DocumentId.ToString(CultureInfo.InvariantCulture) + "/ebook?language=" + Uri.EscapeDataString(book.Language))
                    .Attribute("download", "").Attribute("data-translated-epub", book.Language)
                : new Span(new Text("translation.books.incomplete")).Class("list-value list-value--dim");
            return new ListRow([identity, language, models, new Div(download).Class("list-actions")], [models]);
        }

        private static string LanguageName(string code)
        {
            try { return CultureInfo.GetCultureInfo(code).DisplayName; }
            catch (CultureNotFoundException) { return code; }
        }

        // The computers in their order with what each answers and failed with, the models and the speed test, the browser's own cache.
        private void DefineSetup(Page page, LocalTranslationModelsV1 models)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var translating = Array.FindIndex(models.Machines, machine => machine.Reachable && machine.Installed.Any(models.Approved.Contains));

            // The Ollama machines in order: the first that answers with a model translates, the next takes over when it drops out. Their state is that of the
            // last check (every thirty seconds); Check now asks them at once.
            var machines = new List<IUiComponent> { new P(new Text("translation.setup.machinesHelp")).Class("translation-help") };
            if (!models.Configured) machines.Add(new P(new Text("translation.local.configure")).Attribute("role", "status"));
            machines.AddRange(models.Machines.Select((machine, place) => MachineCard(machine, place, translating, models, csrf)));
            if (models.Configured)
                machines.Add(new Div(new PostForm(Home + "?handler=Check", csrf, [], new Button(new Text("translation.machines.check")).Soft().Submit())
                    .Attribute("data-translation-submit", "check")).Class("page-actions"));
            machines.Add(new Form<TranslationMachinesDataV1>(form =>
            {
                form.Multiline(item => item.Machines, "translation.local.machines");
                form.Save();
            }, Home + "?handler=Machines", csrf, _machines ?? new TranslationMachinesDataV1([.. models.Machines.Select(machine => machine.Address)]),
                _machines is null ? null : FormError));
            page.Content.Add(new UiSection("translation.machines.title", machines, new Icon("network", assets, 18)));

            // The default model of every server translation, among the installed ones; automatic is the largest installed TranslateGemma.
            // A saved model that no machine has now stays listed, so the choice is not lost silently.
            var choices = models.Choices.Concat(models.DefaultModel is { } saved && !models.Choices.Contains(saved, StringComparer.Ordinal) ? [saved] : Array.Empty<string>());
            var modelParts = new List<IUiComponent>
            {
                new Form<TranslationModelDataV1>(form =>
                {
                    form.Select(item => item.Model, "translation.local.defaultModel",
                        [new SelectOption("", texts["translation.local.defaultModel.automatic"]), .. choices.Select(choice => new SelectOption(choice, choice))],
                        new FieldHelp(Info: "translation.local.defaultModelHint"));
                    form.Save();
                }, Home + "?handler=Model", csrf, _model ?? new TranslationModelDataV1(models.DefaultModel), _model is null ? null : FormError)
            };

            // The approved models as the machine that translates now has them; they stay listed while no machine answers.
            foreach (var model in models.Approved)
            {
                var installed = models.Installed.Contains(model, StringComparer.Ordinal);
                var loaded = installed && models.Running.Contains(model, StringComparer.Ordinal);
                var chip = new StateChip(new Text(loaded ? "translation.local.loaded" : installed ? "translation.local.installed" : "translation.local.notInstalled"),
                    loaded ? UiTone.Success : installed ? UiTone.Info : UiTone.Neutral, new Icon(installed ? "check" : "download", assets, 14));
                IUiComponent action = installed && models.Problem is null
                    ? new Div(new PostForm(Home + "?handler=Benchmark&model=" + Uri.EscapeDataString(model), csrf, [],
                        new Button(new Text("translation.local.benchmark")).Soft().Submit()).Attribute("data-translation-submit", "benchmark"),
                        new Span(new Text("translation.local.benchmarkHint")).Class("list-value list-value--dim")).Class("translation-model__action")
                    : new Span();
                modelParts.Add(new Div(new Div(new Strong(new ValueText(model)), chip).Class("translation-model__name"), action).Class("translation-model"));
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
            modelParts.Add(download);
            if (models.DownloadState is "interrupted")
                modelParts.Add(new P(new Text("translation.local.retryInfo")).Attribute("role", "status"));
            if (models.DownloadState is "failed")
                modelParts.Add(new P(new Text("translation.local.failureInfo")).Attribute("role", "alert"));

            // The free space of Ollama's model volume, which only the Owner can know; model downloads are offered up to it.
            modelParts.Add(new P(new ValueText(texts["translation.local.capacity"] + " "
                + Math.Round(models.VerifiedFreeBytes / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB")).Class("translation-help"));
            modelParts.Add(new Form<TranslationFreeBytesDataV1>(form =>
            {
                form.ByteSize(item => item.FreeBytes, "translation.local.freeBytes", new FieldHelp(Info: "translation.local.freeBytesHint"));
                form.Save();
            }, Home + "?handler=FreeBytes", csrf, _freeBytes ?? new TranslationFreeBytesDataV1(models.VerifiedFreeBytes), _freeBytes is null ? null : FormError));
            page.Content.Add(new UiSection("translation.models.title", modelParts, new Icon("layers", assets, 18)));

            if (_benchmark is { } result)
            {
                var samples = new List<IUiComponent> { new P(new Text("translation.local.benchmarkWarning")).Class("translation-help") };
                for (var index = 0; index < result.Samples.Length; index++)
                {
                    samples.Add(new Div(new Span(new ValueText(result.Samples[index])).Class("translation-sample__text"),
                        new Span(new ValueText(result.Seconds[index].ToString("0.0", CultureInfo.InvariantCulture) + " s · "
                            + result.TokensPerSecond[index].ToString("0.0", CultureInfo.InvariantCulture) + " tokens/s")).Class("translation-sample__figures")).Class("translation-sample"));
                }

                page.Content.Add(new UiSection("translation.benchmark.title", samples, new Icon("pulse", assets, 18)));
            }

            // What the browser of the Owner kept of its own Mozilla translations (a week, at most 300 MB): only this browser can empty it.
            if (texts.CanTranslateInBrowser())
                page.Content.Add(new UiSection("translation.cache.title", [
                    new Div(new Button(new Text("translation.browserCache.clear")).Soft().Attribute("type", "button").Attribute("data-translation-clear-browser-cache", ""),
                        new P(new Text("translation.browserCache.cleared")).Attribute("data-translation-browser-cache-cleared", "").Attribute("role", "status").Attribute("hidden", "hidden")).Class("page-actions")
                ], new Icon("drive", assets, 18)));
        }

        // One machine: its place in the order, its address and state, what it answers with, what it failed with last, and the models it can still download.
        private IUiComponent MachineCard(LocalTranslationMachineV1 machine, int place, int translating, LocalTranslationModelsV1 models, string? csrf)
        {
            var installed = machine.Installed.Where(models.Approved.Contains).ToArray();
            var resting = !machine.Reachable && machine.RestingUntil is not null;
            var (stateKey, tone, icon) = machine.CheckedAt is null ? ("translation.machine.unchecked", UiTone.Neutral, "clock")
                : resting ? ("translation.machine.resting", UiTone.Warning, "clock")
                : !machine.Reachable ? ("translation.local.machine.offline", UiTone.Danger, "alert")
                : place == translating ? ("translation.local.machine.translating", UiTone.Success, "check")
                : installed.Length == 0 ? ("translation.machine.noModel", UiTone.Warning, "alert")
                : ("translation.local.machine.waiting", UiTone.Info, "pause");

            var facts = new List<IUiComponent>
            {
                Fact("translation.machine.reachable", new Text(machine.Reachable ? "translation.machine.yes" : "translation.machine.no")),
                Fact("translation.machine.answers", machine.Reachable ? installed.Length > 0 ? new ValueText(string.Join(", ", installed)) : new Text("translation.machine.noApproved") : new Text("translation.machine.none"))
            };
            if (machine.Running.Length > 0) facts.Add(Fact("translation.machine.loaded", new ValueText(string.Join(", ", machine.Running))));
            if (machine.CheckedAt is { } checkedAt) facts.Add(Fact("translation.machine.checkedAt", new LocalTime(checkedAt, "time")));
            if (machine.RestingUntil is { } until && resting) facts.Add(Fact("translation.machine.restingUntil", new LocalTime(until, "time")));
            facts.Add(Fact("translation.machine.lastError", machine.LastError is { } reason
                ? new Span(new Text("translation.machine.error." + reason), machine.LastErrorAt is { } at ? new LocalTime(at) : new Span())
                : new Text("translation.machine.noError")));

            var parts = new List<IUiComponent>
            {
                new Div(new Span(new ValueText((place + 1).ToString(CultureInfo.InvariantCulture))).Class("translation-machine__place"),
                    new Div(new ValueText(machine.Address)).Class("translation-machine__address"),
                    new StateChip(new Text(stateKey), tone, new Icon(icon, assets, 14))).Class("translation-machine__head"),
                new Dl([.. facts]).Class("translation-facts translation-facts--list")
            };
            foreach (var model in models.Approved.Where(model => machine.Reachable && !machine.Installed.Contains(model, StringComparer.Ordinal)))
            {
                var size = model switch { "translategemma:4b" => 3_300_000_000L, "translategemma:12b" => 8_100_000_000L, _ => 17_000_000_000L };
                if (models.VerifiedFreeBytes < size + 2_000_000_000L || models.DownloadState is "queued" or "downloading") continue;
                var retry = models.DownloadModel == model && models.DownloadMachine == machine.Address && models.DownloadState is "interrupted" or "failed";
                parts.Add(new PostForm(Home + "?handler=Pull&model=" + Uri.EscapeDataString(model) + "&machine=" + place.ToString(CultureInfo.InvariantCulture), csrf, [],
                    new Button(new ValueText(texts[retry ? "translation.local.retry" : "translation.local.pull"] + " · " + model + " · "
                        + Math.Round(size / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture) + " GB")).Soft().Submit()).Attribute("data-translation-submit", "pull"));
            }

            return new Div([.. parts]).Class("translation-machine").Attribute("data-translation-machine", machine.Address);
        }

        private static IUiComponent Fact(string labelKey, IUiComponent value) => new Div(new Dt(new Text(labelKey)), new Dd(value)).Class("translation-facts__fact");

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

        public async Task<IActionResult> OnPostCheckAsync()
        {
            await check.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), new NoData(), HttpContext.RequestAborted);
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

        public async Task<IActionResult> OnPostModelAsync(string? model)
        {
            _model = new TranslationModelDataV1(model);
            Tab = "setup";
            await saveModel.ExecuteAsync<UpdateResult>(new LocalTranslationModelsParametersV1(), _model, HttpContext.RequestAborted);
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
            if (Books) _books = await readBooks.ExecuteAsync<PageResult<TranslatedBookV1>>(new TranslatedBooksParametersV1(Math.Max(1, PageNumber)), HttpContext.RequestAborted);
            else if (!Setup) _progress = await readProgress.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: HttpContext.RequestAborted);
        }
    }
}
