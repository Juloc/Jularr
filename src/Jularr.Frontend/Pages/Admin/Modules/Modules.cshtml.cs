using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Modules
{
    // Every module as a card with its switch; the switches are one form, saved together by the page's pill, so a module and what it needs change in one step.
    // The Caching module's card holds its settings and its sub-option modules, saved with the switches.
    public sealed class ModulesModel(ReadInstanceModulesV1 read, ReadCachingSettingsV1 readCaching, UpdateInstanceModulesV1 update, FrontendAssets assets, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/Modules";

        private IReadOnlyList<InstanceModuleDetailsV1> _modules = [];
        private CachingSettingsV1? _caching;
        private UpdateInstanceModulesDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("admin.instance.modules");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/SettingCard.entry.scss");
            page.Asset("Framework/Components/OptionList.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Pages/Admin/Modules/Modules.entry.ts");
            if (_modules.Count == 0 || _caching is not { } caching) return;

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var input = _input ?? new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(_modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))),
                caching.CatalogDetailKeepDays, caching.DetailsDiscoverAhead);
            var byId = _modules.ToDictionary(module => module.Id);
            var enabled = input.Modules.ToDictionary(module => byId[module.Id].Key, module => module.IsEnabled);
            var form = new Form<UpdateInstanceModulesDataV1>(fields =>
            {
                // One module's row; the row keeps its place in the posted list through its id.
                void Row(Form<UpdateInstanceModulesDataV1> target, InstanceModuleDetailsV1 module, Func<IUiComponent, bool, IUiComponent> place) =>
                    target.Rows(item => item.Modules, (row, item) =>
                    {
                        row.Hidden(data => data.Id);
                        row.Hidden(data => data.Version);
                        row.Switch(data => data.IsEnabled, "instance.module." + module.Key, control => place(control, item.IsEnabled));
                    }, item => item.Id == module.Id);

                foreach (var module in _modules)
                {
                    if (module.Key == CachingModule.FullCatalogKey) continue;
                    if (module.Key != CachingModule.Key)
                    {
                        Row(fields, module, (control, isOn) => ModuleCard.Build(module, _modules, control, isOn, enabled, assets, texts));
                        continue;
                    }

                    // The Caching module's switch is the first part of its card, its settings and sub-options the rest.
                    var isOn = enabled.GetValueOrDefault(module.Key);
                    fields.Wrap(parts => new Div(ModuleCard.Build(module, _modules, parts[0], isOn, enabled, assets, texts, [.. parts.Skip(1)])).Class("ui-form__row"), card =>
                    {
                        Row(card, module, (control, _) => control);
                        CachingModule.Parts(card, item => item.CatalogDetailKeepDays, item => item.DetailsDiscoverAhead, subs =>
                        {
                            if (_modules.FirstOrDefault(other => other.Key == CachingModule.FullCatalogKey) is { } full)
                            {
                                Row(subs, full, (control, _) => CachingModule.FullCatalogRow(control, full.Requires, texts));
                            }
                        }, texts);
                    });
                }

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
        public async Task<IActionResult> OnPostAsync(List<ModuleRow> modules, int catalogDetailKeepDays, bool detailsDiscoverAhead)
        {
            _input = new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))),
                catalogDetailKeepDays, detailsDiscoverAhead);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceModulesParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _modules = await read.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
            _caching = await readCaching.ExecuteAsync<CachingSettingsV1>(new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
        }

        public sealed record ModuleRow(long Id, bool IsEnabled, long Version);
    }
}
