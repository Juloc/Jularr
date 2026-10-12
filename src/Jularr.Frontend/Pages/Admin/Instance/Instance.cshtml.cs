using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Instance
{
    public sealed class InstanceModel(ReadInstanceSettingsV1 read, UpdateInstanceSettingsV1 update, ReadUiLocalesV1 readLocales, IAntiforgery antiforgery) : JularrPage
    {
        private InstanceSettingsDetailsV1? _settings;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateInstanceSettingsDataV1? _input;

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
                    form.Submit("common.save");
                }, Url.Page("/Admin/Instance/Instance"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version), Error));

                if (TempData["InstanceSaved"] is true)
                {
                    page.Content.Add(new P(new Text("admin.instance.saved")).Attribute("role", "status"));
                }
            }
            else if (Error is not null)
            {
                page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long uiLocaleId, long version)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceSettingsParametersV1(), _input, HttpContext.RequestAborted);
            TempData["InstanceSaved"] = true;
            return RedirectToPage("/Admin/Instance/Instance");
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _settings = await read.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), token);
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
        }
    }
}
