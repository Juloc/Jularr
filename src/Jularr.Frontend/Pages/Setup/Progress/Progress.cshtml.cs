using System.Security;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Progress
{
    // Resumes the wizard at its step and moves it on.
    public sealed class ProgressModel(ReadSetupProgressV1 read, UpdateSetupProgressV1 update) : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("setup.title");
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Only the Owner configures the instance.
            SetupProgressV1 progress;
            try
            {
                progress = await read.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), HttpContext.RequestAborted);
            }
            catch (SecurityException)
            {
                return LocalRedirect("/");
            }

            return LocalRedirect(progress.IsCompleted ? "/" : SetupWizard.Paths[progress.SetupStepTypeId]);
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] SetupStepType setupStepTypeId)
        {
            await update.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new UpdateSetupProgressDataV1(setupStepTypeId), HttpContext.RequestAborted);
            return LocalRedirect(SetupWizard.Paths[setupStepTypeId]);
        }
    }
}
