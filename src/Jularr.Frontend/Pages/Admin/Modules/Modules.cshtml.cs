using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Modules
{
    // Every module as a card with its switch; the switches are one form, saved together by the page's pill, so a module and what it needs change in one step.
    public sealed class ModulesModel(ReadInstanceModulesV1 read, UpdateInstanceModulesV1 update, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/Modules";

        private IReadOnlyList<InstanceModuleDetailsV1> _modules = [];
        private UpdateInstanceModulesDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("admin.instance.modules");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/SettingCard.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Pages/Admin/Modules/Modules.entry.ts");
            if (_modules.Count == 0) return;

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var input = _input ?? new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(_modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            var byId = _modules.ToDictionary(module => module.Id);
            var enabled = input.Modules.ToDictionary(module => byId[module.Id].Key, module => module.IsEnabled);
            var form = new Form<UpdateInstanceModulesDataV1>(fields =>
            {
                fields.Rows(item => item.Modules, (row, item) =>
                {
                    var module = byId[item.Id];
                    row.Hidden(data => data.Id);
                    row.Hidden(data => data.Version);
                    row.Switch(data => data.IsEnabled, "instance.module." + module.Key, control => ModuleCard.Build(module, _modules, control, item.IsEnabled, enabled, assets, texts));
                });
                fields.Save();
            }, Address, csrf, input, FormError);
            page.Content.Add(new Div(form).Class("setting-cards"));

            // What belongs to a module but is not a switch: the speech models of the reader's read-aloud.
            page.Content.Add(new ActionDock(assets, "admin.modules.actions", [new A(new Icon("headphones", assets, 16), new Span(new Text("speech.models.title")).Class("action-dock__text"))
                .Class("button").Attribute("href", "/Admin/Instance/Speech")]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Every module comes back as shown, so one save is one consistent step.
        public async Task<IActionResult> OnPostAsync(List<ModuleRow> modules)
        {
            _input = new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceModulesParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _modules = await read.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
        }

        public sealed record ModuleRow(long Id, bool IsEnabled, long Version);
    }
}
