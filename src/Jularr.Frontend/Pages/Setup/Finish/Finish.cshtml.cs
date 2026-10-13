using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Setup.Finish
{
    // What still blocks, what deserves a look, and the end of the wizard.
    public sealed class FinishModel(ReadSetupProgressV1 read, CompleteSetupV1 complete, IAntiforgery antiforgery) : JularrPage
    {
        private SetupProgressV1? _progress;

        protected override void Define(Page page)
        {
            page.Title("setup.title");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(SetupWizard.Frame(SetupStepType.Finish, csrf));
            if (_progress is not { } progress)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            if (progress.Blockers.Length > 0)
            {
                page.Content.Add(new UiSection("setup.blockers", [.. progress.Blockers.Select(Check)]));
            }

            if (progress.Warnings.Length > 0)
            {
                page.Content.Add(new UiSection("setup.warnings", [.. progress.Warnings.Select(Check)]));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            if (progress.Blockers.Length == 0)
            {
                page.Content.Add(new Form<CompleteSetupDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Submit("setup.finish");
                }, "/Setup/Finish", csrf, new CompleteSetupDataV1(progress.Version)));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_progress?.IsCompleted is true) return LocalRedirect("/");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long version)
        {
            await complete.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new CompleteSetupDataV1(version), HttpContext.RequestAborted);
            return LocalRedirect("/");
        }

        protected override async Task LoadDataAsync()
        {
            _progress = await read.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), HttpContext.RequestAborted);
        }

        // Library checks name their media type or subtype.
        private static IUiComponent Check(string key) => key.StartsWith("library.", StringComparison.Ordinal)
            ? new P(new Text("media." + key["library.".Length..]), new ValueText(": "), new Text("setup.check.library"))
            : new P(new Text("setup.check." + key));
    }
}
