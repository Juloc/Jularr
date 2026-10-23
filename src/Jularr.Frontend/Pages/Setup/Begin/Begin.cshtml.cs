using Jularr.Backend.Data.Admin.Backup.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Backup.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Begin
{
    // The first choice: start empty, take over what Sonarr, Radarr, Readarr and Lidarr hold, or restore a backup (offered only where a restore can run).
    public sealed class BeginModel(UpdateSetupStartV1 update, ReadBackupsV1 readBackups, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private bool _canRestore;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Begin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            IUiComponent Choice(SetupStartType start, string icon, string labelKey) => new PostForm("/Setup/Begin?handler=Start", csrf, [("start", start.ToString())],
                new Button(new IconTile(new Icon(icon, assets, 20), UiTone.Accent), new Strong(new Text(labelKey))).Submit().Class("setup-choice"));

            var choices = new List<IUiComponent>
            {
                Choice(SetupStartType.New, "plus", "setup.begin.new"),
                Choice(SetupStartType.Takeover, "swap", "setup.begin.takeover")
            };
            if (_canRestore) choices.Add(new A(new IconTile(new Icon("history", assets, 20), UiTone.Accent), new Strong(new Text("setup.begin.restore"))).Class("setup-choice").Attribute("href", "/Setup/Backup"));
            page.Content.Add(new Div([.. choices]).Class("setup-choices"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostStartAsync([BindRequired] SetupStartType start)
        {
            await update.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new UpdateSetupStartDataV1(start), HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Progress");
        }

        protected override async Task LoadDataAsync()
        {
            _canRestore = (await readBackups.ExecuteAsync<BackupsV1>(new BackupsParametersV1(), HttpContext.RequestAborted)).CanRestore;
        }
    }
}
