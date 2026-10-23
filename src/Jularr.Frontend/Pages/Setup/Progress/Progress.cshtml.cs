using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Progress
{
    // Resumes the wizard at the step last reached and moves it on from a step without a form of its own.
    public sealed class ProgressModel(ReadSetupProgressV1 read, UpdateSetupProgressV1 update, SaveSetupLibrariesV1 saveLibraries, UiText texts) : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("setup.title");
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // The gate has sent everyone but the Owner elsewhere; a finished setup leads home.
            var progress = await read.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), HttpContext.RequestAborted);
            return LocalRedirect(progress.IsCompleted ? "/" : SetupWizard.Resume(progress, texts));
        }

        // The step is done: what it needs is saved, the flow continues with the next one that applies.
        public async Task<IActionResult> OnPostAsync([BindRequired] string place)
        {
            var ct = HttpContext.RequestAborted;
            var progress = await read.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), ct);
            if (SetupWizard.After(progress, texts, place) is not { } next) return LocalRedirect(SetupWizard.Resume(progress, texts));

            // Media without a library path switch off before the flow leaves the Libraries step.
            if (place == "Libraries") await saveLibraries.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new CompleteSetupDataV1(progress.Version), ct);
            await update.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new UpdateSetupProgressDataV1(next.Step ?? SetupStepType.Begin), ct);
            return LocalRedirect(next.Path);
        }
    }
}
