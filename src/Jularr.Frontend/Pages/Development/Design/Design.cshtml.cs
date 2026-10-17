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
            page.Asset("Framework/Components/MediaCard.entry.ts");
            page.Asset("Modules/Appearance/appearance.entry.scss");

            // Shared appearance controls.
            page.Content.Add(new Panel(items =>
            {
                items.Add(new P(new Text("design.introduction")));
                items.Add(new Appearance());
            }));
            if (_pane is "cover" or "library-continue" or "card-experiments")
            {
                page.Content.Add(new NavigationTabs([
                    new("design.study.baseline", "/Design?pane=cover#media-variants", _pane == "cover"),
                    new("design.study.title", "/Design?pane=library-continue#library-variants", _pane == "library-continue"),
                    new("design.experiment.title", "/Design?pane=card-experiments#library-variants", _pane == "card-experiments")
                ], "nav.tabs"));
            }
            if (_pane == "concepts")
            {
                DefineMediaConcepts(page);
                return;
            }
            if (_pane == "cover")
            {
                DefineCoverPreview(page);
                return;
            }
            if (_pane is "library-continue" or "card-experiments")
            {
                DefineLibraryContinue(page, _pane == "card-experiments");
                return;
            }
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
                new Div(new UiSection("design.mediaVariants", [
                    new A(new Text("design.mediaConcepts")).Class("button button--primary").Attribute("href", "/Design?pane=concepts#media-variants")
                ])).Attribute("id", "media-variants"),
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
                        .Availability(new MediaAvailability(assets, MediaAvailabilityState.Requested)).Genres(new Text("design.tagGenre"))
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

        private void DefineMediaConcepts(Page page)
        {
            // Workshop-only layout compositions.
            var layouts = new[] { "poster", "caption", "pill", "row", "cinema", "editorial", "album", "gallery", "dock", "ticket", "rail", "timeline" };
            var cards = layouts.Select((layout, index) => (IUiComponent)new Section(
                new H3(new ValueText((index + 1).ToString("00", CultureInfo.InvariantCulture) + " · "), new Text("design.concept." + layout)),
                new Div(Concept(layout)).Class("design-lab__concept-canvas")).Class("design-lab__concept-option")).ToArray();
            page.Content.Add(new P(new Text("design.mediaNotice")));
            page.Content.Add(new Div(new UiSection("design.mediaConcepts", [new Div(cards).Class("design-lab__concept-grid")])).Attribute("id", "media-variants"));

            // One real preview control.
            page.Content.Add(MediaPreview.Host(assets, texts));
            page.Content.Add(new MediaPreview("concept-preview", assets, "Your Name.", assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"),
                assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg"), "2016", texts["design.conceptSummary"])
                .Trailer("k4xGqY5IDBE").Content(new P(new Text("design.exampleStatus"))));

            IUiComponent Concept(string layout)
            {
                var landscape = layout is "cinema" or "dock" or "timeline";
                var image = landscape ? "your-name-backdrop.jpg" : "your-name-poster.jpg";
                var availability = new MediaAvailability(assets, MediaAvailabilityState.Available);

                // Reuse canonical UI pieces.
                var art = new A(new MediaImage(assets.ImageUrl("Pages/Development/Design/Images/" + image), "Your Name.", landscape ? 1280 : 342, landscape ? 720 : 513))
                    .Class("design-lab__concept-art").Attribute("href", "/Design?pane=concepts#media-variants")
                    .Attribute("data-preview-open", "concept-preview").Attribute("aria-haspopup", "dialog");
                var heading = new Div(new Span(new ValueText("Your Name.")).Class("design-lab__concept-title"),
                    new Span(new ValueText("2016 · "), new Text("media.type.Movie")).Class("design-lab__concept-meta")).Class("design-lab__concept-heading");
                var statuses = new Div(new A(new Tag(new Text("media.series.Ended")).Neutral()).Class("design-lab__concept-series")
                        .Attribute("href", "/Design?pane=concepts#media-variants").Attribute("data-preview-open", "concept-preview").Attribute("aria-haspopup", "dialog"),
                    new IconInfo(availability, availability.GetLabel(texts)).Preview("concept-preview", "/Design?pane=concepts#media-variants"))
                    .Class("design-lab__concept-status");
                var genres = new Div(new Tag(new Text("design.genreDrama")).Neutral(), new Tag(new Text("design.genreRomance")).Neutral()).Class("design-lab__concept-genres");
                var actions = new Div(new Button(new Icon("player-play", assets), new Text("media.continue")).Primary().Disabled(),
                    new A(new Icon("bookmark", assets), new Text("media.details")).Class("button")
                        .Attribute("href", "/Design?pane=concepts#media-variants").Attribute("data-preview-open", "concept-preview").Attribute("aria-haspopup", "dialog"))
                    .Class("design-lab__concept-actions");

                var content = new List<IUiComponent> { art, heading, statuses, genres, actions };
                if (layout is "cinema" or "editorial" or "ticket")
                    content.Add(new P(new Text("design.conceptSummary")).Class("design-lab__concept-summary"));
                if (layout == "timeline")
                    content.Add(new ConceptProgress().Attribute("max", "100").Attribute("value", "42").Attribute("aria-label", texts["media.progress"]).Class("media-card__progress design-lab__concept-progress"));

                return new Div([.. content]).Class("design-lab__concept design-lab__concept--" + layout);
            }
        }

        private void DefineCoverPreview(Page page)
        {
            page.Content.Add(new P(new Text("design.coverNotice")));
            var states = new[] { "available", "downloading", "searching", "partial", "requested", "none" };
            page.Content.Add(new Div(states.Select(state => Card(state)).ToArray()).Class("design-cover-grid").Attribute("id", "media-variants"));

            IUiComponent Card(string state)
            {
                // Production control, illustrative data.
                var card = new MediaCard("Your Name.", "/Design?pane=cover#media-variants", assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"),
                    "2016", "media.state.NotRequested")
                    .SeriesStatus("media.series.Ended", new Icon("calendar", assets)).Information(texts["design.conceptSummary"], "8.4").Trailer("k4xGqY5IDBE")
                    .Genres(new Tag(new Icon("reading", assets), new Text("design.genreDrama")).Tone("blue"),
                        new Tag(new Icon("star", assets), new Text("design.genreRomance")).Tone("pink"))
                    .Actions(new Button(new Icon(state is "requested" or "none" ? "requests" : "player-play", assets),
                            new Text(state is "requested" or "none" ? "request.create" : "player.play")).Primary().Disabled(),
                        new Button(new Icon("bookmark", assets)).Disabled().Attribute("aria-label", texts["nav.watchlist"]));
                if (state != "none")
                {
                    var availability = state switch
                    {
                        "available" => MediaAvailabilityState.Available,
                        "downloading" => MediaAvailabilityState.Downloading,
                        "searching" => MediaAvailabilityState.Searching,
                        "partial" => MediaAvailabilityState.Partial,
                        _ => MediaAvailabilityState.Requested
                    };
                    card.Availability(new MediaAvailability(assets, availability, 18, 24));
                }
                return card;
            }
        }

        private void DefineLibraryContinue(Page page, bool fresh)
        {
            if (fresh)
            {
                DefineIndicatorRows(page);
                return;
            }

            var route = "/Design?pane=" + _pane;
            page.Content.Add(new P(new Text("design.study.notice")));
            page.Content.Add(new NavigationTabs([
                new("nav.library", route + "#library-variants", false),
                new("discover.continueWatching", route + "#continue-variants", false)
            ], "nav.tabs"));

            // Same card, local compositions.
            foreach (var kind in new[] { "library", "continue" })
            {
                var layouts = kind == "library" ? new[] { "edge", "capsule", "side", "footer", "stack" } : new[] { "edge", "dock", "timeline", "focus", "minimal" };
                var samples = layouts.Select((layout, index) =>
                {
                    var preferred = index is 0 or 2 or 4 ? new MediaLanguage("flag-de", "design.languageGerman") : null;
                    var availability = kind == "library" && index == 2 ? MediaAvailabilityState.Partial : MediaAvailabilityState.Available;
                    var card = new MediaCard("Your Name.", route + "#" + kind + "-variants",
                        assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"), "2016", "media.state.InProgress")
                        .SeriesStatus("media.series.Ended", new Icon("calendar", assets)).Availability(new MediaAvailability(assets, availability, availability == MediaAvailabilityState.Partial ? 18 : 24, 24))
                        .Information(texts["design.conceptSummary"], "8.4").Continue(1800000, 5400000).Trailer("k4xGqY5IDBE")
                        .Languages(assets, preferred, 3, index == 4 ? null : preferred, 6)
                        .Genres(new Tag(new Icon("reading", assets), new Text("design.genreDrama")).Tone("blue"),
                            new Tag(new Icon("star", assets), new Text("design.genreRomance")).Tone("pink"))
                        .Actions(new Button(new Icon("player-play", assets), new Text(kind == "continue" ? "media.continue" : "player.play")).Primary().Disabled(),
                            new Button(new Icon("bookmark", assets)).Disabled().Attribute("aria-label", texts["nav.watchlist"]));

                    var key = "design.study." + kind + "." + layout;
                    return (IUiComponent)new Section(
                        new H3(new ValueText((index + 1).ToString("00", CultureInfo.InvariantCulture) + " · "), new Text(key)),
                        new P(new Text(key + ".hint")),
                        new Div(card).Class("card-study__sample card-study__sample--" + kind + " card-study__sample--" + kind + "-" + layout)
                    ).Class("card-study__option");
                }).ToArray();
                page.Content.Add(new Div(new UiSection(kind == "library" ? "nav.library" : "discover.continueWatching",
                    [new Div(samples).Class("card-study__grid")])).Attribute("id", kind + "-variants"));
            }
        }

        private void DefineIndicatorRows(Page page)
        {
            page.Asset("Pages/Development/Design/Design.entry.ts");
            page.Content.Add(new P(new Text("design.experiment.notice")));
            var layouts = new[] { "combined", "inset", "editorial", "cinema", "rail", "frame", "centered" };

            // Explicit sample states, no business data.
            var examples = new (MediaAvailabilityState State, int Available, long Watched)[]
            {
                (MediaAvailabilityState.ProfileAudio, 24, 1800000),
                (MediaAvailabilityState.OtherAudio, 24, 900000),
                (MediaAvailabilityState.Partial, 12, 0),
                (MediaAvailabilityState.PartialOtherAudio, 12, 0),
                (MediaAvailabilityState.Downloading, 6, 0),
                (MediaAvailabilityState.Searching, 0, 0),
                (MediaAvailabilityState.Requested, 0, 0),
                (MediaAvailabilityState.Missing, 0, 0)
            };
            var rows = layouts.Select(layout =>
            {
                var cards = examples.Select(example =>
                {
                    var previewId = "experiment-preview-" + example.State;
                    var card = new MediaCard("Your Name.", "/Design?pane=card-experiments#library-variants",
                        assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"), "2016", "media.state.NotRequested")
                        .Minimal().Preview(previewId)
                        .Availability(new MediaAvailability(assets, example.State, example.Available, 24))
                        .Information(null, "8.4").Trailer("k4xGqY5IDBE")
                        .Actions(new Button(new Icon(example.Available > 0 ? "player-play" : "requests", assets),
                                new Text(example.Watched > 0 ? "media.continue" : example.Available > 0 ? "player.play" : "media.request"))
                                .Primary().Compact()
                                .Attribute("data-preview-open", previewId).Attribute("aria-haspopup", "dialog"),
                            new Button(new Icon("library", assets), new Text("media.details")).Compact().Attribute("data-preview-open", previewId).Attribute("aria-haspopup", "dialog"))
                        .Bookmark(new Button(new Icon("bookmark", assets)).IconOnly(texts["nav.watchlist"])
                            .Attribute("data-design-bookmark", "").Attribute("aria-pressed", "false"));

                    // One card, supplied optional context.
                    if (example.Watched > 0) card.Continue(example.Watched, 5400000);

                    return (IUiComponent)new Div(card).Class("card-experiment card-experiment--" + layout);
                }).ToArray();
                return (IUiComponent)new Div(
                    new MediaShelf(assets, "design.experiment." + layout, null, cards),
                    new P(new Text("design.experiment." + layout + ".hint"))
                ).Attribute("id", "indicator-" + layout);
            }).ToArray();
            page.Content.Add(new Div(rows).Attribute("id", "library-variants"));

            // Reuse real preview interaction.
            page.Content.Add(MediaPreview.Host(assets, texts));
            foreach (var example in examples)
            {
                page.Content.Add(new MediaPreview("experiment-preview-" + example.State, assets, "Your Name.",
                    assets.ImageUrl("Pages/Development/Design/Images/your-name-poster.jpg"),
                    assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg"), "2016 · 8.4", texts["design.cardSynopsis"])
                    .Trailer("k4xGqY5IDBE").Content(new P(new Text("design.experiment.previewNotice")),
                        new MediaAvailability(assets, example.State, example.Available, 24)));
            }
        }

        public IActionResult OnGet(string? pane)
        {
            if (!environment.IsDevelopment()) return NotFound();
            if (pane is not null and not ("components" or "layouts" or "concepts" or "cover" or "library-continue" or "card-experiments")) return NotFound();

            _pane = pane ?? "components";
            PreparePage();
            ViewData["ShellPreview"] = true;
            return Page();
        }

        private sealed class Fieldset(params IUiComponent[] content) : HtmlElement("fieldset", content);
        private sealed class ConceptProgress() : HtmlElement("progress");
    }
}
