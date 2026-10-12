using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Languages
{
    public sealed class LanguagesModel(ReadUiLocalesV1 locales, CreateUiLocaleV1 create, UpdateUiLocaleV1 update, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<UiLocaleDetailsV1>? _locales;
        private CreateUiLocaleDataV1? _creation;
        private UpdateUiLocaleDataV1? _update;
        private long? _localeId;

        protected override void Define(Page page)
        {
            page.Title("admin.languages.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Languages/Languages.entry.scss");
            page.Content.Add(new A(new Text("admin.localization.title")).Attribute("href", "/Admin/Localization"));
            var states = new[] { new SelectOption("True", texts["admin.languages.enabled"]), new SelectOption("False", texts["admin.languages.disabled"]) };
            var directions = new[] { new SelectOption("False", texts["admin.languages.leftToRight"]), new SelectOption("True", texts["admin.languages.rightToLeft"]) };

            // Create real language metadata.
            page.Content.Add(new Form<CreateUiLocaleDataV1>(form =>
            {
                form.Text(item => item.Locale, "admin.languages.code");
                form.Text(item => item.Name, "admin.languages.name");
                form.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                form.Submit("admin.languages.add");
            }, "/Admin/Languages?handler=Create", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                _creation ?? new CreateUiLocaleDataV1("", "", false), _creation is null ? null : Error));

            foreach (var locale in _locales?.Items ?? [])
            {
                page.Content.Add(new P(new ValueText(locale.Locale)));
                page.Content.Add(new Form<UpdateUiLocaleDataV1>(form =>
                {
                    form.Hidden(item => item.CatalogVersion);
                    form.Text(item => item.Name, "admin.languages.name");
                    form.Select(item => item.IsEnabled, "admin.languages.state", states);
                    form.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                    form.Submit("common.save");
                }, "/Admin/Languages?uiLocaleId=" + locale.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _localeId == locale.Id ? _update : new UpdateUiLocaleDataV1(locale.Name, locale.IsEnabled, locale.IsRightToLeft, locale.CatalogVersion),
                    _localeId == locale.Id ? Error : null));
            }

            if (_locales?.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Languages?pageNumber=" + (_locales.Page - 1)));
            if (_locales is { } && _locales.Items.Count == _locales.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Languages?pageNumber=" + (_locales.Page + 1)));
            if (Error is not null && _localeId is null && _creation is null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostCreateAsync(string? locale, string? name, [BindRequired] bool isRightToLeft)
        {
            _creation = new CreateUiLocaleDataV1(locale ?? "", name ?? "", isRightToLeft);
            await create.ExecuteAsync<CreateUiLocaleResultV1>(_creation, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Languages/Languages");
        }

        public async Task<IActionResult> OnPostAsync(long uiLocaleId, string? name, [BindRequired] bool isEnabled, [BindRequired] bool isRightToLeft, long catalogVersion)
        {
            _localeId = uiLocaleId;
            _update = new UpdateUiLocaleDataV1(name ?? "", isEnabled, isRightToLeft, catalogVersion);
            await update.ExecuteAsync<UpdateResult>(new UpdateUiLocaleParametersV1(uiLocaleId), _update, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Languages/Languages");
        }

        protected override async Task LoadDataAsync()
        {
            _locales = await locales.ExecuteAsync<PageResult<UiLocaleDetailsV1>>(new ReadUiLocalesParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
