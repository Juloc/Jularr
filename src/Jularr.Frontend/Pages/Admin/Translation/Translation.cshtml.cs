using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Translation.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Translation
{
    public sealed class TranslationModelPage(ReadLocalTranslationModelsV1 read, PullLocalTranslationModelV1 pull,
        BenchmarkLocalTranslationModelV1 benchmark, IAntiforgery antiforgery) : JularrPage
    {
        private const string Home = "/Admin/Instance/Translation";
        private LocalTranslationModelsV1? _models;
        private LocalTranslationBenchmarkV1? _benchmark;

        protected override void Define(Page page)
        {
            page.Title("reader.language");
            page.Navigation("nav.admin");
            page.Content.Add(new P(new ValueText("Optional local translation models — stored by Ollama outside the Jularr image.")));
            page.Content.Add(new A(new ValueText("Back to instance settings")).Attribute("href", "/Admin/Instance"));
            if (_models is null) return;
            if (!_models.Configured)
            {
                page.Content.Add(new P(new ValueText("To enable local models, configure LocalTranslation__Endpoint with the private Ollama URL (for example http://ollama:11434).")).Attribute("role", "status"));
                return;
            }

            if (_models.Problem is { } problem)
            {
                page.Content.Add(new P(new ValueText(problem)).Attribute("role", "alert"));
                return;
            }

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(new ValueText("Model downloads consume persistent disk space in Ollama. Check free capacity of that runtime volume before downloading.")));
            foreach (var model in _models.Approved)
            {
                var installed = _models.Installed.Contains(model, StringComparer.Ordinal);
                var running = _models.Running.Contains(model, StringComparer.Ordinal);
                var size = model switch { "translategemma:4b" => "3.3 GB", "translategemma:12b" => "8.1 GB", _ => "17 GB" };
                var description = model + " (approx. " + size + " download, more RAM needed) — " + (installed ? running ? "loaded" : "installed" : "not installed");
                page.Content.Add(new Div(
                    new P(new ValueText(description)),
                    installed
                        ? new PostForm(Home + "?handler=Benchmark&model=" + Uri.EscapeDataString(model), csrf, [],
                            new Button(new ValueText("Run translation benchmark")).Submit())
                        : new PostForm(Home + "?handler=Pull&model=" + Uri.EscapeDataString(model), csrf, [],
                            new Button(new ValueText("Download into Ollama")).Submit())
                ).Class("feature-card"));
            }

            if (_benchmark is { } result)
            {
                page.Content.Add(new P(new ValueText("Smoke benchmark for " + result.Model
                    + " (timing only; literary translation accuracy is NOT verified).")));
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
