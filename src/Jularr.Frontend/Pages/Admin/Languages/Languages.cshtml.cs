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
    // The interface languages as a list of the list kit: a row each with its state and an edit drawer; adding one and the text catalog are actions of the dock.
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
            page.Asset("Pages/Admin/Languages/Languages.entry.scss");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var states = new[] { new SelectOption("True", texts["admin.languages.enabled"]), new SelectOption("False", texts["admin.languages.disabled"]) };
            var directions = new[] { new SelectOption("False", texts["admin.languages.leftToRight"]), new SelectOption("True", texts["admin.languages.rightToLeft"]) };

            // One row per language; its drawer edits the name, the state and the direction.
            var items = _locales?.Items ?? [];
            var columns = new List<ListColumn> { new("admin.languages.title", ListRole.Main), new("table.status", ListRole.Status), new("admin.languages.direction", ListRole.Data, 1), new("common.actions", ListRole.Actions) };
            page.Content.Add(new ListPanel("admin.languages.list", columns, [.. items.Select(Row)]).Modifier("languages-list"));
            page.Content.AddRange(items.Select(locale => Edit(locale, csrf, states, directions)));

            // A new language starts with its code, name and direction.
            var added = _creation is not null;
            page.Content.Add(new Drawer(assets, "locale-new", [new DrawerPane("add", "admin.languages.add", "admin.languages.add", "plus", [new Form<CreateUiLocaleDataV1>(form =>
            {
                form.Text(item => item.Locale, "admin.languages.code");
                form.Text(item => item.Name, "admin.languages.name");
                form.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                form.Submit("admin.languages.add");
            }, Address + "?handler=Create", csrf, _creation ?? new CreateUiLocaleDataV1("", "", false), added ? FormError : null)], [])], sheet: true, openOnLoad: added && FormError is not null));
            if (FormError is not null && _localeId is null && _creation is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

            var actions = new List<IUiComponent> { Drawer.Opener(new Button(new Icon("plus", assets, 16), new Span(new Text("admin.languages.add")).Class("action-dock__text")).Primary(), "locale-new") };
            if (texts.Can("localization.read")) actions.Add(new A(new Icon("globe", assets, 16), new Span(new Text("admin.localization.title")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Localization"));
            var paging = _locales is { } result && (result.Page > 1 || result.Items.Count == result.PageSize)
                ? new DockPaging(result.Page > 1 ? Link(result.Page - 1) : null, result.Items.Count == result.PageSize ? Link(result.Page + 1) : null,
                    new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dock.pageOnly"], result.Page)))
                : null;
            page.Content.Add(new ActionDock(assets, "admin.languages.actions", actions, paging: paging));
        }

        private ListRow Row(UiLocaleDetailsV1 locale)
        {
            var drawer = "locale-" + locale.Id.ToString(CultureInfo.InvariantCulture);
            var identity = new ListIdentity(new IconTile(new Icon("globe", assets, 18), UiTone.Accent), ListIdentity.TitleButton(new ValueText(locale.Name), drawer), new ValueText(locale.Locale));
            var state = locale.IsEnabled
                ? new StateChip(new Text("admin.languages.enabled"), UiTone.Success, new Icon("check", assets, 14))
                : new StateChip(new Text("admin.languages.disabled"), UiTone.Neutral, new Icon("power", assets, 14));
            var direction = new Text(locale.IsRightToLeft ? "admin.languages.rightToLeft" : "admin.languages.leftToRight");
            var edit = Drawer.Opener(new Button(new Icon("pencil", assets, 14), new Text("common.edit")).Compact(), drawer);
            return new ListRow([identity, state, direction, new Div(edit).Class("list-actions")], [ListPanel.Fact("admin.languages.direction", direction)]);
        }

        // The drawer of one language: the same form as ever, with the item named by the drawer's title row.
        private IUiComponent Edit(UiLocaleDetailsV1 locale, string csrf, SelectOption[] states, SelectOption[] directions)
        {
            var failed = _localeId == locale.Id;
            var form = new Form<UpdateUiLocaleDataV1>(fields =>
            {
                fields.Hidden(item => item.CatalogVersion);
                fields.Text(item => item.Name, "admin.languages.name");
                fields.Select(item => item.IsEnabled, "admin.languages.state", states);
                fields.Select(item => item.IsRightToLeft, "admin.languages.direction", directions);
                fields.Submit("common.save");
            }, Address + "?uiLocaleId=" + locale.Id.ToString(CultureInfo.InvariantCulture), csrf,
                failed ? _update : new UpdateUiLocaleDataV1(locale.Name, locale.IsEnabled, locale.IsRightToLeft, locale.CatalogVersion), failed ? FormError : null);
            var body = new List<IUiComponent> { Drawer.State("admin.languages.code", new ValueText(locale.Locale)), form };
            return new Drawer(assets, "locale-" + locale.Id.ToString(CultureInfo.InvariantCulture), [new DrawerPane("edit", "admin.languages.edit", "admin.languages.edit", "pencil", body, [])], sheet: true, openOnLoad: failed && FormError is not null);
        }

        private static string Link(int page) => page > 1 ? Address + "?pageNumber=" + page.ToString(CultureInfo.InvariantCulture) : Address;

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
