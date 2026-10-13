using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Instance
{
    public sealed class InstanceModel(ReadInstanceSettingsV1 read, UpdateInstanceSettingsV1 update, ReadUiLocalesV1 readLocales,
        ReadInstanceModulesV1 readModules, UpdateInstanceModuleV1 updateModule, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private InstanceSettingsDetailsV1? _settings;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateInstanceSettingsDataV1? _input;
        private IReadOnlyList<InstanceModuleDetailsV1> _modules = [];
        private UpdateInstanceModuleDataV1? _moduleInput;
        private long? _moduleId;

        protected override void Define(Page page)
        {
            page.Title("admin.instance.title");
            page.Navigation("nav.admin");

            // Edit supported settings only.
            if (_settings is { } settings)
            {
                var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                page.Content.Add(new Form<UpdateInstanceSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.Name, "admin.instance.name");
                    form.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                    form.Number(item => item.ReadingCacheMegabytes, "admin.instance.readingCacheMegabytes");
                    form.Number(item => item.ReadingCacheWorks, "admin.instance.readingCacheWorks");
                    form.Select(item => item.UsersConfirmSources, "admin.instance.usersConfirmSources",
                        [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                    form.Submit("common.save");
                }, Url.Page("/Admin/Instance/Instance"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, settings.ReadingCacheMegabytes, settings.ReadingCacheWorks, settings.UsersConfirmSources), _moduleId is null ? FormError : null));

                if (TempData["InstanceSaved"] is true)
                {
                    page.Content.Add(new P(new Text("admin.instance.saved")).Attribute("role", "status"));
                }
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }

            // Only implemented module controls.
            var stateOptions = new[] { new SelectOption("True", texts["admin.providers.enabled"]), new SelectOption("False", texts["admin.providers.disabled"]) };
            var moduleForms = new List<IUiComponent> { new P(new Text("admin.instance.module.retained")) };
            foreach (var module in _modules)
            {
                moduleForms.Add(new P(new Text("instance.module." + module.Key)));
                moduleForms.Add(new Form<UpdateInstanceModuleDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Select(item => item.IsEnabled, "admin.instance.module.state", stateOptions);
                    form.Submit("common.save");
                }, Url.Page("/Admin/Instance/Instance", "Module", new { instanceModuleId = module.Id }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _moduleId == module.Id ? _moduleInput : new UpdateInstanceModuleDataV1(module.IsEnabled, module.Version), _moduleId == module.Id ? FormError : null));
            }

            if (_modules.Count > 0) page.Content.Add(new UiSection("admin.instance.modules", moduleForms));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks,
            bool? usersConfirmSources)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version, readingCacheMegabytes, readingCacheWorks, usersConfirmSources);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceSettingsParametersV1(), _input, HttpContext.RequestAborted);
            TempData["InstanceSaved"] = true;
            return RedirectToPage("/Admin/Instance/Instance");
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _settings = await read.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), token);
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
            _modules = await readModules.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), token);
        }

        public async Task<IActionResult> OnPostModuleAsync(long instanceModuleId, [BindRequired] bool isEnabled, long version)
        {
            _moduleId = instanceModuleId;
            _moduleInput = new UpdateInstanceModuleDataV1(isEnabled, version);
            await updateModule.ExecuteAsync<UpdateResult>(new UpdateInstanceModuleParametersV1(instanceModuleId), _moduleInput, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Instance/Instance");
        }
    }
}
