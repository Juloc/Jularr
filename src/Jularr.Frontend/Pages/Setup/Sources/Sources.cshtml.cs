using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Setup.Sources
{
    public sealed class SourcesModel(ReadSetupProgressV1 readProgress, ReadSetupSourcesV1 readSources, SaveSetupSourcesV1 save,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private SetupProgressV1? _progress;
        private SetupSourcesDetailsV1? _sources;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Sources");
            page.Asset("Pages/Setup/Sources/Sources.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(SetupWizard.Frame(SetupStepType.Providers, showReturn: false));
            if (_progress is not { } progress) return;

            page.Content.Add(new Form<SaveSetupSourcesDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Section("setup.sources.metadata", section =>
                {
                    section.Password(item => item.TmdbKey, "setup.sources.tmdbKey", "new-password");
                    section.Content(new P(new Text(_sources?.HasTmdbKey is true ? "admin.providers.secretSet" : "admin.providers.secretMissing")));
                    section.Content(new A(new Text("setup.sources.tmdbAccount")).Attribute("href", "https://www.themoviedb.org/settings/api").Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"));
                });

                if (texts.Can(null, "Acquisition"))
                {
                    form.Content(new UiSection("setup.sources.acquisition", [new Div(
                        Link("setup.step.Indexers", "/Setup/Indexers"), Link("setup.step.Downloads", "/Setup/Downloads"),
                        Link("setup.step.Profiles", "/Setup/Profiles"), Link("setup.step.Media", "/Setup/Takeover")
                    ).Class("setup-sources__links")]));
                }

                form.Content(new UiSection("setup.sources.providers", [Link("setup.sources.plex", "/Setup/Providers")]));
                if (progress.Blockers.Length > 0) form.Content(new UiSection("setup.blockers", [.. progress.Blockers.Select(Check)]));
                if (progress.Warnings.Length > 0) form.Content(new UiSection("setup.warnings", [.. progress.Warnings.Select(Check)]));
                form.Submit("setup.finish");
            }, "/Setup/Sources", csrf, new SaveSetupSourcesDataV1(progress.Version, null), FormError));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_progress?.IsCompleted is true) return LocalRedirect("/");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long version, string? tmdbKey)
        {
            await save.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new SaveSetupSourcesDataV1(version, tmdbKey), HttpContext.RequestAborted);
            return LocalRedirect("/");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(cancellationToken: ct);
            _sources = await readSources.ExecuteAsync<SetupSourcesDetailsV1>(cancellationToken: ct);
        }

        private static IUiComponent Link(string key, string path) => new A(new Text(key)).Class("button").Attribute("href", path);

        private static IUiComponent Check(string key) => key.StartsWith("library.", StringComparison.Ordinal)
            ? new P(new Text("media." + key["library.".Length..]), new ValueText(": "), new Text("setup.check.library"))
            : new P(new Text("setup.check." + key));
    }
}
