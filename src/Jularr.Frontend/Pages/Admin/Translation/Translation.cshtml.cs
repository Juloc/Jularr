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
    public sealed class TranslationModelPage(ReadLocalTranslationModelsV1 read, PullLocalTranslationModelV1 pull,
        BenchmarkLocalTranslationModelV1 benchmark, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Home = "/Admin/Instance/Translation";
        private LocalTranslationModelsV1? _models;
        private LocalTranslationBenchmarkV1? _benchmark;

        protected override void Define(Page page)
        {
            page.Title("translation.local.title");
            page.Navigation("nav.admin");
            page.Content.Add(new P(new Text("translation.local.description")));
            page.Content.Add(new A(new Text("translation.local.back")).Attribute("href", "/Admin/Instance"));
            if (_models is null) return;
            if (!_models.Configured)
            {
                page.Content.Add(new P(new Text("translation.local.configure")).Attribute("role", "status"));
                return;
            }

            if (_models.Problem is not null)
            {
                page.Content.Add(new P(new Text("translation.local.offline")).Attribute("role", "alert"));
                return;
            }

            page.Content.Add(new P(new Text("translation.local.capacity")));
            if (_models.DownloadState is not "idle")
            {
                page.Content.Add(new P(new ValueText(texts["translation.local.state." + _models.DownloadState] + ": "
                    + _models.DownloadModel + " (" + _models.DownloadProgress.ToString(CultureInfo.InvariantCulture) + "%)"))
                    .Attribute("role", "status"));
            }

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            foreach (var model in _models.Approved)
            {
                var installed = _models.Installed.Contains(model, StringComparer.Ordinal);
                var running = _models.Running.Contains(model, StringComparer.Ordinal);
                var size = model switch { "translategemma:4b" => 3_300_000_000L, "translategemma:12b" => 8_100_000_000L, _ => 17_000_000_000L };
                var sizeText = Math.Round(size / 1e9, 1).ToString("0.0", CultureInfo.InvariantCulture);
                var state = installed ? running ? "translation.local.loaded" : "translation.local.installed" : "translation.local.notInstalled";
                var canPull = !installed && _models.VerifiedFreeBytes >= size + 2_000_000_000L && _models.DownloadState is not ("queued" or "downloading");

                page.Content.Add(new Div(
                    new P(new ValueText(model + " · " + sizeText + " GB · " + texts[state])),
                    installed
                        ? new PostForm(Home + "?handler=Benchmark&model=" + Uri.EscapeDataString(model), csrf, [],
                            new Button(new Text("translation.local.benchmark")).Submit())
                        : canPull
                            ? new PostForm(Home + "?handler=Pull&model=" + Uri.EscapeDataString(model), csrf, [],
                                new Button(new Text("translation.local.pull")).Submit())
                            : new P(new Text("translation.local.pullBlocked"))
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

        public async Task<IActionResult> OnPostPullAsync(string model)
        {
            await pull.ExecuteAsync<UpdateResult>(new LocalTranslationModelParametersV1(model), new NoData(), HttpContext.RequestAborted);
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
        }
    }
}
