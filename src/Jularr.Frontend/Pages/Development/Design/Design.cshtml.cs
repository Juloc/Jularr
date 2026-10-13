using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
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
            page.Asset("Framework/Components/MediaShelf.entry.ts");
            page.Asset("Framework/Components/MediaPreview.entry.ts");
            page.Asset("Modules/Appearance/appearance.entry.scss");

            // Shared appearance controls.
            page.Content.Add(new Panel(items =>
            {
                items.Add(new P(new Text("design.introduction")));
                items.Add(new Appearance());
            }));
            page.Content.Add(new NavigationTabs([
                new("design.controls", "/Design#controls", false), new("design.forms", "/Design#forms", false),
                new("design.media", "/Design#media", false), new("design.player", "/Design#player", false)
            ], "nav.tabs"));

            // Live brand and artwork.
            var originalArtwork = new (string File, string Key, int Width, int Height)[]
            {
                ("washi.webp", "design.art.washi", 1024, 1024),
                ("blossom-branch.webp", "design.art.blossom", 600, 900),
                ("hero-fuji.webp", "design.art.fuji", 2000, 667),
                ("hero-torii.webp", "design.art.torii", 2000, 667),
                ("heroine.webp", "design.art.mascot", 720, 900)
            };
            page.Content.Add(new UiSection("design.branding", [
                new Brand(assets, login: true),
                new Div(originalArtwork.Select(art => (IUiComponent)new Div(
                    new MediaImage(assets.ImageUrl("Modules/Appearance/Original/" + art.File), texts[art.Key], art.Width, art.Height),
                    new P(new Text(art.Key))).Class("design-lab__art")).ToArray()).Class("design-lab__artwork")
            ]));

            // Button and icon states.
            page.Content.Add(new Div(new UiSection("design.controls", [
                new Div(new Button(new Icon("home", assets), new Text("design.primary")).Primary(),
                    new Button(new Text("design.secondary")), new Button(new Text("design.disabled")).Disabled(),
                    new Button(new Text("design.selected")).Attribute("aria-pressed", "true")).Class("design-lab__buttons"),
                new Div(new Tag(new Icon("library", assets), new Text("media.type.Movie")), new Tag(new Text("design.tagGenre")), new Tag(new Icon("subtitles", assets), new Text("design.tagSubtitles")),
                    new Tag(new Icon("star", assets), new Text("design.selected")).Accent()).Class("design-lab__buttons"),
                new P(new Text("design.toggleNotice")),
                new Div(new Toggle("design.toggleOff"), new Toggle("design.toggleOn", true),
                    new Toggle("design.disabled", true, true)).Class("design-lab__buttons")
            ])).Attribute("id", "controls"));

            // Original Jularr icon shapes.
            var iconNames = new[] { "home", "library", "bookmark", "calendar", "settings", "user", "search", "bell", "copy", "star",
                "delete", "reading", "books", "admin", "users", "requests", "download", "folder", "link", "subtitles", "sync",
                "activity", "scan", "logs", "spark", "globe", "key", "server", "palette", "history", "devices", "sign-out",
                "pulse", "theme-system", "theme-light", "theme-dark", "providers", "chevron-down", "chevron-left", "chevron-up", "headphones" };
            page.Content.Add(new UiSection("design.icons", [
                new Div(iconNames.Select(name => (IUiComponent)new Div(new Icon(name, assets), new Span(new ValueText(name)))
                    .Class("design-lab__icon")).ToArray()).Class("design-lab__icons")
            ]));

            // Canonical field metadata.
            var data = new CreateWorkDataV1(MediaType.Movie, texts["design.exampleTitle"], 2026);
            var options = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type])).ToArray();
            var error = ServiceError.FromException(new DataValidationException(nameof(data.CanonicalTitle), "required"));
            page.Content.Add(new Div(new UiSection("design.forms", [new Split(split =>
            {
                split.Primary.Add(WorkForm(data));
                split.Primary.Add(new Form<LoginDataV1>(form => form.Password(item => item.Password, "account.field.password").Submit("account.login.submit")));
                split.Primary.Add(new Form<UpdateInstanceModuleDataV1>(form => form.Toggle(item => item.IsEnabled, "design.selected"),
                    data: new UpdateInstanceModuleDataV1(true, 1)));
                split.Secondary.Add(new H3(new Text("design.validationExample")));
                split.Secondary.Add(WorkForm(new CreateWorkDataV1(MediaType.Movie, ""), error));
            })])).Attribute("id", "forms"));

            // Typed table and layouts.
            var rows = new[]
            {
                new WorkDetailsV1(1, MediaType.Movie, null, false, texts["design.exampleTitle"], 2026, 1),
                new WorkDetailsV1(2, MediaType.Book, MediaSubtype.Manga, false, texts["design.exampleOtherTitle"], 2025, 1)
            };
            var table = new Table<WorkDetailsV1>(rows).Text(item => item.CanonicalTitle, "media.title")
                .Content("media.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId))).Text(item => item.ReleaseYear, "media.year");
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

            // Labeled media examples.
            var previews = new List<IUiComponent>();
            AddPreview("preview-your-name-library", "Your Name.", "your-name-poster.jpg", "2016", "request.status.Completed", MediaAvailabilityState.Available);
            AddPreview("preview-suzume-library", "Suzume", "suzume-poster.jpg", "2022", "request.status.Completed", MediaAvailabilityState.Available);
            AddPreview("preview-your-name-discover", "Your Name.", "your-name-poster.jpg", "2016", "media.state.NotRequested", MediaAvailabilityState.Missing);
            AddPreview("preview-suzume-discover", "Suzume", "suzume-poster.jpg", "2022", "request.status.Approved", MediaAvailabilityState.Downloading);
            page.Content.Add(new Div(new UiSection("design.media", [
                new MediaHero(texts["design.exampleTitle"], assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg"), texts["design.exampleContext"],
                    texts["design.exampleOverview"], "design.primary", "/Design#player", "/Design#media"),
                new MediaHero(texts["design.exampleTitle"], null, texts["design.exampleContext"], texts["design.exampleOverview"], "media.details", "/Design#media"),
                new P(new Text("design.mediaNotice")),
                new MediaShelf(assets, "discover.continueWatching", null, [
                    new MediaCard("Your Name.", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"), "2016", "media.state.InProgress", "media.continue")
                        .Continue(3000000, 6360000),
                    new MediaCard("Suzume", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/suzume-poster.jpg"), "2022", "media.state.InProgress", "media.continue")
                        .Continue(900000, 7320000)
                ]),
                new MediaShelf(assets, "design.mediaLibrary", null, [
                    new MediaCard("Your Name.", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"), "2016", "media.state.Available")
                        .Preview("preview-your-name-library")
                        .Availability(new MediaAvailability(assets, MediaAvailabilityState.Available))
                        .Genres(new Text("design.genreDrama"), new Text("design.genreRomance"))
                        .Languages(assets, new MediaLanguage("flag-de", "design.languageGerman"), 3, new MediaLanguage("flag-de", "design.languageGerman"), 8),
                    new MediaCard("Suzume", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/suzume-poster.jpg"), "2022", "media.state.Available")
                        .Preview("preview-suzume-library")
                        .Availability(new MediaAvailability(assets, MediaAvailabilityState.Available))
                        .Genres(new Text("design.tagGenre"), new Text("design.genreAdventure")).Languages(assets, null, 2, null, 6),
                    SeriesExample("Continuing", MediaAvailabilityState.Partial, 18, 24),
                    SeriesExample("Releasing", MediaAvailabilityState.Searching),
                    SeriesExample("Ended", MediaAvailabilityState.Available),
                    SeriesExample("Canceled", MediaAvailabilityState.Missing),
                    SeriesExample("Announced", MediaAvailabilityState.Downloading)
                ]),
                new MediaShelf(assets, "design.mediaDiscover", null, [
                    new MediaCard("Your Name.", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"), "2016", "media.state.NotRequested", "request.create")
                        .Preview("preview-your-name-discover")
                        .Availability(new MediaAvailability(assets, MediaAvailabilityState.Missing)).Genres(new Text("design.genreDrama")),
                    new MediaCard("Suzume", "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/suzume-poster.jpg"), "2022", "media.state.Requested")
                        .Preview("preview-suzume-discover")
                        .Availability(new MediaAvailability(assets, MediaAvailabilityState.Downloading)).Genres(new Text("design.tagGenre"))
                ]),
                new P(new Text("design.imageCredit"), new ValueText(" "),
                    new A(new ValueText("TMDB")).Attribute("href", "https://www.themoviedb.org/movie/372058/images")),
                new P(new Text("design.trailerCredit"), new ValueText(" "),
                    new A(new ValueText("TOHO")).Attribute("href", "https://www.youtube.com/watch?v=k4xGqY5IDBE"))
            ])).Attribute("id", "media"));
            page.Content.Add(MediaPreview.Host(assets, texts));
            page.Content.AddRange(previews);

            // Read-only playback chrome.
            var source = new PlayerSource("Your Name.", texts["design.exampleContext"], "/Design#player", "", "", "",
                assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg"));
            page.Content.Add(new Div(new UiSection("design.player", [
                new P(new Text("design.playerNotice")),
                new Fieldset(new UiPlayer(source, assets)).Attribute("disabled", "disabled").Attribute("aria-label", texts["design.playerNotice"])
            ])).Class("design-lab__player").Attribute("id", "player"));

            // Explicitly illustrative series states.
            MediaCard SeriesExample(string status, MediaAvailabilityState availability, int available = 0, int monitored = 0)
            {
                var id = "preview-series-" + status;
                var requestStatus = availability == MediaAvailabilityState.Missing ? "media.state.NotRequested" : "request.status.Approved";
                AddPreview(id, texts["design.exampleTitle"], "suzume-poster.jpg", texts["design.exampleContext"], requestStatus, availability, "media.series." + status);
                return new MediaCard(texts["design.exampleTitle"], "/Design#media", assets.ImageUrl("Pages/Development/Design/Images/suzume-poster.jpg"), null, "design.exampleStatus")
                    .Preview(id)
                    .SeriesStatus("media.series." + status).Availability(new MediaAvailability(assets, availability, available, monitored))
                    .Genres(new Text("design.tagGenre"), new Text("design.genreAdventure"))
                    .Languages(assets, new MediaLanguage("flag-de", "design.languageGerman"), 2, null, 6);
            }

            void AddPreview(string id, string title, string poster, string meta, string requestStatus, MediaAvailabilityState availability, string? seriesStatus = null)
            {
                // Separate request and acquisition.
                var statuses = new List<IUiComponent>();
                if (seriesStatus is not null) statuses.Add(Status("media.preview.seriesStatus", new Tag(new Text(seriesStatus))));
                statuses.Add(Status("media.preview.availability", new MediaAvailability(assets, availability, availability == MediaAvailabilityState.Partial ? 18 : 0, 24)));
                statuses.Add(Status("media.preview.requestStatus", new Tag(new Icon("requests", assets), new Text(requestStatus))));
                var downloading = availability == MediaAvailabilityState.Downloading;
                statuses.Add(Status("media.preview.downloadStatus", new Tag(new Icon("download", assets), new Text(downloading ? "request.status.Downloading" : "media.preview.noDownload"))));

                var preview = new MediaPreview(id, assets, title, assets.ImageUrl("Pages/Development/Design/Images/" + poster),
                    assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg"), meta, texts["design.exampleOverview"])
                    .Content(new P(new Text("design.exampleStatus")), new Div([.. statuses]).Class("media-preview__statuses"))
                    .Actions(new Button(new Icon("requests", assets), new Text("request.create")).Primary().Disabled(),
                        new Button(new Icon("bookmark", assets), new Text("media.watchlist.add")).Disabled(),
                        new A(new Text("media.details")).Class("button").Attribute("href", "/Design#media"));
                if (title == "Your Name.") preview.Trailer("k4xGqY5IDBE");
                previews.Add(preview);
            }

            static IUiComponent Status(string labelKey, IUiComponent value) =>
                new Div(new Span(new Text(labelKey)).Class("media-preview__label"), value).Class("media-preview__status");

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
