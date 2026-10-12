using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Development.Design
{
    [AllowAnonymous]
    public sealed class DesignModel(IHostEnvironment environment, FrontendAssets assets, UiText texts) : JularrPage
    {
        private string _pane = "components";

        protected override void Define(Page page)
        {
            page.Title("design.title");
            page.Asset("Pages/Development/Design/Design.entry.scss");
            page.Asset("Modules/Appearance/appearance.entry.scss");

            // Shared appearance controls.
            page.Content.Add(new Panel(items =>
            {
                items.Add(new P(new Text("design.introduction")));
                items.Add(new Appearance());
            }));

            // Live brand and artwork.
            page.Content.Add(new UiSection("design.branding", [
                new Brand(assets, login: true),
                new Div(
                    new Div().Class("design-lab__art design-lab__art--landscape"),
                    new Div().Class("design-lab__art design-lab__art--mascot"))
                    .Class("design-lab__artwork").Attribute("aria-hidden", "true")
            ]));

            // Button and icon states.
            page.Content.Add(new Div(new UiSection("design.controls", [
                new Div(new Button(new Icon("home", assets), new Text("design.primary")).Primary(),
                    new Button(new Text("design.secondary")), new Button(new Text("design.disabled")).Disabled(),
                    new Button(new Text("design.selected")).Attribute("aria-pressed", "true")).Class("design-lab__buttons"),
                new Div(new[] { "home", "library", "bookmark", "calendar", "settings", "user", "search", "bell" }
                    .Select(name => (IUiComponent)new Icon(name, assets)).ToArray()).Class("design-lab__icons")
            ])).Attribute("id", "controls"));

            // Canonical field metadata.
            var data = new CreateWorkDataV1(MediaType.Movie, texts["design.exampleTitle"], 2026);
            var options = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type])).ToArray();
            var error = ServiceError.FromException(new DataValidationException(nameof(data.CanonicalTitle), "required"));
            page.Content.Add(new UiSection("design.forms", [new Split(split =>
            {
                split.Primary.Add(WorkForm(data));
                split.Primary.Add(new Form<LoginDataV1>(form => form.Password(item => item.Password, "account.field.password").Submit("account.login.submit")));
                split.Secondary.Add(new H3(new Text("design.validationExample")));
                split.Secondary.Add(WorkForm(new CreateWorkDataV1(MediaType.Movie, ""), error));
            })]));

            // Typed table and layouts.
            var rows = new[]
            {
                new WorkDetailsV1(1, MediaType.Movie, texts["design.exampleTitle"], 2026, 1),
                new WorkDetailsV1(2, MediaType.Book, texts["design.exampleOtherTitle"], 2025, 1)
            };
            var table = new Table<WorkDetailsV1>(rows).Text(item => item.CanonicalTitle, "media.title")
                .Resource(item => item.MediaTypeId, "media.type", type => "media.type." + type).Text(item => item.ReleaseYear, "media.year");
            page.Content.Add(new Div(new UiSection("design.layouts", [
                new Tabs(tabs =>
                {
                    tabs.Add("design.controls", "/Design?pane=components#layouts", _pane == "components", items => items.Add(table));
                    tabs.Add("design.layouts", "/Design?pane=layouts#layouts", _pane == "layouts", items => items.Add(new Split(split =>
                    {
                        split.Primary.Add(new Panel(content => content.Add(new P(new Text("design.primaryArea")))));
                        split.Secondary.Add(new Panel(content => content.Add(new P(new Text("design.secondaryArea")))));
                    }).MasterDetail(true, "/Design?pane=components#layouts")));
                }),
                new NavigationTabs([new("design.controls", "/Design#controls", true), new("design.media", "/Design#media", false)], "nav.tabs"),
                new UiSubnav([new("design.controls", "/Design#controls"), new("design.media", "/Design#media")]),
                new ItemList("design.primaryArea", "design.secondaryArea"),
                new SearchBox("/Design", "design.search", null)
            ])).Attribute("id", "layouts"));

            // Media and empty artwork.
            page.Content.Add(new Div(new UiSection("design.media", [
                new MediaHero(texts["design.exampleTitle"], null, texts["design.exampleContext"], texts["design.exampleOverview"], "media.details", "/Design#media"),
                new MediaShelf("design.media", null, [
                    new MediaCard(texts["design.exampleTitle"], "/Design#media", null, texts["design.exampleContext"], "design.exampleStatus"),
                    new MediaCard(texts["design.exampleOtherTitle"], "/Design#media", null, texts["design.exampleContext"], "design.exampleStatus", "media.details", "/Design#media")
                ])
            ])).Attribute("id", "media"));

            // Read-only playback chrome.
            var source = new PlayerSource(texts["design.exampleTitle"], texts["design.exampleContext"], "/Design#player", "", "", "");
            page.Content.Add(new Div(new UiSection("design.player", [
                new P(new Text("design.playerNotice")),
                new Fieldset(new UiPlayer(source, assets)).Attribute("disabled", "disabled").Attribute("aria-label", texts["design.playerNotice"])
            ])).Class("design-lab__player").Attribute("id", "player"));

            // Earlier approved references.
            page.Content.Add(new UiSection("design.references", [
                new P(new Text("design.referenceNotice")),
                new Div(
                    new MediaImage(assets.ImageUrl("Pages/Development/Design/References/appearance-approved.png"), texts["design.appearanceReference"], 1536, 1024),
                    new MediaImage(assets.ImageUrl("Pages/Development/Design/References/library-approved.png"), texts["design.libraryReference"], 1448, 1086))
                    .Class("design-lab__references")
            ]));

            Form<CreateWorkDataV1> WorkForm(CreateWorkDataV1 example, ServiceError? failure = null) => new(form =>
                form.Text(item => item.CanonicalTitle, "media.title").Select(item => item.MediaTypeId, "media.type", options)
                    .Number(item => item.ReleaseYear, "media.year").Checkbox("design.selected").Submit("common.save"), data: example, error: failure);
        }

        public IActionResult OnGet(string? pane)
        {
            if (!environment.IsDevelopment()) return NotFound();
            if (pane is not null and not ("components" or "layouts")) return NotFound();

            _pane = pane ?? "components";
            PreparePage();
            ViewData["ShellPreview"] = true;
            return Page();
        }

        private sealed class Fieldset(params IUiComponent[] content) : HtmlElement("fieldset", content);
    }
}
