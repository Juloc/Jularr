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
    // The interface languages as cards: each holds its name, state and direction as inputs, saved by the page's pill; adding one is a dialog and the text catalog an action of the dock.
    public sealed class LanguagesModel(ReadUiLocalesV1 locales, CreateUiLocaleV1 create, UpdateUiLocaleV1 update, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/Languages";
        private PageResult<UiLocaleDetailsV1>? _locales;
        private CreateUiLocaleDataV1? _creation;
        private UpdateUiLocaleDataV1? _update;
        private long? _localeId;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.languages.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var directions = new[] { new SelectOption("False", texts["admin.languages.leftToRight"]), new SelectOption("True", texts["admin.languages.rightToLeft"]) };

            // One card per language; its inputs are the name, the state and the direction.
            var items = _locales?.Items ?? [];
            page.Content.Add(new CardGrid([.. items.Select(locale => Card(locale, csrf, directions))]).Paged(items.Count, _locales?.PageSize ?? 25));

            // A new language starts with its code, name and direction.
            var added = _creation is not null;
            page.Content.Add(new Modal(assets, "locale-new", "admin.languages.add", [new Form<CreateUiLocaleDataV1>(form =>
            {
                form.Text(item => item.Locale, "admin.languages.code");
                form.Text(item => item.Name, "admin.languages.name");
                form.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                form.Cancel();
                form.Submit("admin.languages.add");
            }, Address + "?handler=Create", csrf, _creation ?? new CreateUiLocaleDataV1("", "", false), added ? FormError : null)], open: added && FormError is not null));
            if (FormError is not null && _localeId is null && _creation is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

            var actions = new List<IUiComponent> { Modal.Opener(new Button(new Icon("plus", assets, 16), new Span(new Text("admin.languages.add")).Class("action-dock__text")).Primary(), "locale-new") };
            if (texts.Can("localization.read")) actions.Add(new A(new Icon("globe", assets, 16), new Span(new Text("admin.localization.title")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Localization"));
            page.Content.Add(new ActionDock(assets, "admin.languages.actions", actions));
        }

        // One language: its name and code and its state in the head, the inputs under them; a card with an id swaps alone.
        private IUiComponent Card(UiLocaleDetailsV1 locale, string? csrf, SelectOption[] directions)
        {
            var failed = _localeId == locale.Id;
            var state = locale.IsEnabled
                ? new StateChip(new Text("admin.languages.enabled"), UiTone.Success, new Icon("check", assets, 14))
                : new StateChip(new Text("admin.languages.disabled"), UiTone.Neutral, new Icon("power", assets, 14));
            var form = new Form<UpdateUiLocaleDataV1>(fields =>
            {
                fields.Hidden(item => item.CatalogVersion);
                fields.Text(item => item.Name, "admin.languages.name");
                fields.Toggle(item => item.IsEnabled, "admin.languages.enabled");
                fields.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                fields.Save();
            }, Address + "?uiLocaleId=" + locale.Id.ToString(CultureInfo.InvariantCulture), csrf,
                failed ? _update : new UpdateUiLocaleDataV1(locale.Name, locale.IsEnabled, locale.IsRightToLeft, locale.CatalogVersion), failed ? FormError : null);
            return new FeatureCard(new IconTile(new Icon("globe", assets, 18), UiTone.Accent), locale.Name, locale.Locale, state, [form], [], null, "locale-" + locale.Id.ToString(CultureInfo.InvariantCulture));
        }

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
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostAsync(long uiLocaleId, string? name, [BindRequired] bool isEnabled, [BindRequired] bool isRightToLeft, long catalogVersion)
        {
            _localeId = uiLocaleId;
            _update = new UpdateUiLocaleDataV1(name ?? "", isEnabled, isRightToLeft, catalogVersion);
            await update.ExecuteAsync<UpdateResult>(new UpdateUiLocaleParametersV1(uiLocaleId), _update, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _locales = await locales.ExecuteAsync<PageResult<UiLocaleDetailsV1>>(new ReadUiLocalesParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
