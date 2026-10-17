using System.Globalization;
using UiDesigner.Framework;

namespace UiDesigner.Projects.Jularr.Reader
{
    public sealed class ReaderPreview(FrontendAssets assets, UiText texts, string antiforgeryToken)
    {
        public void Build(Page page)
        {
            page.Asset("Projects/Jularr/Reader/ReaderPreview.entry.scss");
            var chapters = SampleChapters();
            var illustration = assets.ImageUrl("Projects/Jularr/Player/Images/your-name-backdrop.jpg");

            // One frame for the document.
            page.Content.Add(new Section(
                new Header(
                    new Div(new Icon("books", assets), new Div(new Strong(new ValueText("The Last Lantern")), new Small(new ValueText(chapters[0].Title)).Attribute("data-reader-title", ""),
                        new Small().Attribute("data-reader-message", "").Attribute("role", "status").Attribute("hidden", "hidden"))).Class("reader-preview__heading"),
                    new Nav(Control("library", "preview.reader.contents", "reader-contents"), Control("search", "preview.reader.search", "reader-search"),
                        new Button(new Icon("bookmark", assets)).IconOnly(texts["preview.reader.bookmark"]).Attribute("data-reader-bookmark", "").Attribute("aria-pressed", "false"),
                        Control("reading", "preview.reader.appearance", "reader-appearance"), Control("globe", "preview.reader.language", "reader-language"),
                        Control("settings", "preview.reader.more", "reader-more"))
                        .Class("reader-preview__tools").Attribute("aria-label", texts["preview.reader.tools"])
                ).Class("reader-preview__top"),
                new Button(new Icon("reading", assets), new Text("preview.reader.showControls")).Attribute("data-reader-restore", "").Attribute("hidden", "hidden").Class("button reader-preview__restore"),
                new Aside(new Div(new Icon("books", assets), new Strong(new ValueText("The Last Lantern")), new Small(new Text("preview.reader.original"))).Class("reader-preview__book"),
                    new Div().Attribute("data-reader-sidebar", "")).Class("reader-preview__sidebar"),
                new Div(new Div(chapters.Select((chapter, index) =>
                {
                    var section = new Article(new Small(new ValueText((index + 1).ToString(CultureInfo.InvariantCulture))).Class("reader-preview__eyebrow"),
                        new Div(new MediaImage(illustration, "", 1200, 675)).Class("reader-preview__illustration"),
                        new H1(new ValueText(chapter.Title)), new Div(chapter.Paragraphs.Select(text => (IUiComponent)new P(new ValueText(text))).ToArray()).Class("reader-preview__prose"),
                        new Div(new P(new Text("preview.reader.samplePages")),
                            new Div(Enumerable.Range(0, 2).Select(number => (IUiComponent)new Div(
                                new MediaImage(illustration, "", 600, 900), new MediaImage(illustration, "", 600, 900),
                                new Small(new ValueText((index * 2 + number + 1).ToString(CultureInfo.InvariantCulture))))
                                .Class("reader-preview__image-page")).ToArray()).Class("reader-preview__pages")).Class("reader-preview__visuals"))
                        .Class("reader-preview__chapter").Attribute("data-reader-chapter", index.ToString(CultureInfo.InvariantCulture));
                    if (index > 0) section.Attribute("hidden", "hidden");
                    return (IUiComponent)section;
                }).ToArray()).Class("reader-preview__paper"),
                    new Button(new Icon("chevron-left", assets)).IconOnly(texts["preview.reader.previousPage"]).Attribute("data-reader-turn", "-1").Class("button button--icon reader-preview__turn reader-preview__turn--previous"),
                    new Button(new Span(new Icon("chevron-left", assets)).Class("reader-preview__next-icon")).IconOnly(texts["preview.reader.nextPage"])
                        .Attribute("data-reader-turn", "1").Class("button button--icon reader-preview__turn reader-preview__turn--next")).Class("reader-preview__canvas"),
                new Footer(
                    Control("library", "preview.reader.contents", "reader-contents"),
                    new Button(new Icon("chevron-left", assets)).IconOnly(texts["preview.reader.previous"]).Disabled().Attribute("data-reader-step", "-1"),
                    new Div(new Span(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["preview.reader.position"], 1, chapters.Length))).Attribute("data-reader-position", ""),
                        new Small().Attribute("data-reader-page-position", "").Attribute("aria-live", "polite"),
                        new Input().Attribute("type", "range").Attribute("min", "0").Attribute("max", (chapters.Length - 1).ToString(CultureInfo.InvariantCulture))
                            .Attribute("step", "1").Attribute("value", "0").Attribute("data-reader-progress", "")
                            .Attribute("aria-label", texts["preview.reader.positionLabel"])).Class("reader-preview__position"),
                    new Button(new Span(new Icon("chevron-left", assets)).Class("reader-preview__next-icon")).IconOnly(texts["preview.reader.next"]).Attribute("data-reader-step", "1"),
                    Control("reading", "preview.reader.appearance", "reader-appearance"),
                    new Button(new Icon("player-fullscreen", assets)).IconOnly(texts["player.fullscreen"]).Attribute("data-reader-fullscreen", "")
                ).Class("reader-preview__bottom"),
                Panel("reader-contents", "preview.reader.contents", new Nav(chapters.Select((chapter, index) => (IUiComponent)new Button(new ValueText(chapter.Title))
                    .Attribute("data-reader-jump", index.ToString(CultureInfo.InvariantCulture)).Attribute("aria-current", index == 0 ? "page" : "false")).ToArray())
                    .Class("reader-preview__contents").Attribute("data-reader-contents-list", "")),
                Panel("reader-search", "preview.reader.search", new Div(
                    new SearchBox("/Preview/reader", "preview.reader.find", null), new P(new Text("preview.reader.searchHint")),
                    new Div().Attribute("data-reader-results", "").Attribute("aria-live", "polite"))),
                Panel("reader-appearance", "preview.reader.appearance", Settings()),
                Panel("reader-language", "preview.reader.language", new Div(new Strong(new Text("preview.reader.originalLanguage")), new P(new Text("preview.reader.languageNotice")))),
                Panel("reader-more", "preview.reader.more", new Div(
                    new Button(new Icon("reading", assets), new Text("preview.reader.clean")).Attribute("data-reader-clean", "").Attribute("aria-pressed", "false"),
                    new P(new Text("preview.reader.scopeNotice"))))
            ).Class("reader-preview").Attribute("data-reader-preview", "").Attribute("data-position-template", texts["preview.reader.position"])
                .Attribute("data-reader-layout", "auto").Attribute("data-reader-reading-mode", "paged").Attribute("data-reader-mode", "text").Attribute("data-reader-theme", "paper")
                .Attribute("data-search-empty", texts["preview.reader.noResults"]).Attribute("data-fullscreen-error", texts["preview.reader.noFullscreen"])
                .Attribute("data-page-search-notice", texts["preview.reader.pageSearchNotice"]).Attribute("data-page-template", texts["preview.reader.pagePosition"]));
        }

        private IUiComponent Settings() => new Form<ReaderPreviewData>(form => form
            .Select(data => data.Preset, "preview.reader.preset", [new("book", texts["preview.reader.book"]), new("light-novel", texts["preview.reader.lightNovel"]), new("manga", texts["preview.reader.manga"])])
            .Select(data => data.ContentMode, "preview.reader.contentMode", [new("text", texts["preview.reader.textContent"]), new("pages", texts["preview.reader.pageContent"])])
            .Select(data => data.ReadingMode, "preview.reader.readingMode", [new("paged", texts["preview.reader.paged"]), new("scroll", texts["preview.reader.infiniteScroll"])])
            .Select(data => data.FontFamily, "preview.reader.font", [new("serif", texts["preview.reader.serif"]), new("sans", texts["preview.reader.sans"])])
            .Number(data => data.FontSize, "preview.reader.fontSize")
            .Range(data => data.LineHeight, "preview.reader.lineHeight")
            .Range(data => data.TextWidth, "preview.reader.width")
            .Select(data => data.Layout, "preview.reader.pageLayout", [new("auto", texts["preview.reader.autoPages"]), new("single", texts["preview.reader.single"]), new("spread", texts["preview.reader.spread"])])
            .Select(data => data.Direction, "preview.reader.direction", [new("ltr", texts["preview.reader.ltr"]), new("rtl", texts["preview.reader.rtl"])])
            .Select(data => data.ReaderTheme, "preview.reader.colour", [new("clean", texts["preview.reader.cleanSurface"]), new("paper", texts["preview.reader.paper"])])
            .Text(data => data.SurfaceColor, "preview.reader.surfaceColor")
            .Checkbox(data => data.Illustrations, "preview.reader.illustrations"),
            "/_static-sample", antiforgeryToken, new ReaderPreviewData());

        private IUiComponent Control(string icon, string label, string panel) => new Button(new Icon(icon, assets)).IconOnly(texts[label]).Attribute("popovertarget", panel);

        private IUiComponent Panel(string id, string title, IUiComponent content) => new Div(
            new Header(new H2(new Text(title)), new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", id).Attribute("popovertargetaction", "hide")), content
        ).Class("reader-preview__panel").Attribute("id", id).Attribute("popover", "auto").Attribute("role", "dialog").Attribute("aria-label", texts[title]);

        // Original illustrative text.
        private static ReaderChapter[] SampleChapters() =>
        [
            new("The light across the water", [
                "By the time Mara reached the harbour, the last ferry had gone. A single lantern still burned at the end of the pier, its reflection trembling in the dark water. She stood beneath it and unfolded the letter once more.",
                "There was no address, only a sketch of a house and three words written in blue ink: before the winter. The paper smelled faintly of cedar. It had travelled farther than she had, and it seemed in no hurry to explain itself.",
                "Across the bay, the windows of the old observatory began to shine. Nobody had lived there for years. Mara counted the lights, slowly, until she realised that someone on the other shore was counting hers.",
                "She raised the lantern. One light answered. Then another. The pattern was familiar, like a melody remembered from a room she had not entered since childhood. For the first time all evening, the distance across the water felt small.",
                "Behind her, the town settled into its ordinary sounds: a bicycle on the cobbles, a closing door, the bell above the bakery. Tomorrow she would need a boat. Tonight she could wait, and watch, and let the light finish its sentence."
            ]),
            new("A map of small things", [
                "The boat builder kept her maps in a biscuit tin. They showed no roads and very few names, only the places where the tide changed its mind. Mara spread one across the workbench and held its corners with smooth stones.",
                "The observatory was drawn in the margin, smaller than a thumbprint. Beside it stood a little cedar tree. It was the same tree she had seen on the letter, down to the branch that leaned towards the sea.",
                "They left when the morning mist began to lift. From the water, the town looked like a collection of things somebody had meant to put away: bright roofs, narrow chimneys, a yellow stairway ending at the shore."
            ]),
            new("Before the winter", [
                "At the top of the hill, the observatory door was open. Warm air carried the smell of tea and old books. Mara set the lantern on the threshold, where its light had once been measured against the stars.",
                "The letter had not asked her to recover anything. It had asked her to arrive. Inside, a table was laid for two, and beside the second cup lay a blank sheet of paper.",
                "Outside, the first leaves crossed the garden wall. There was still time before the winter, and this time she knew where to begin."
            ])
        ];

        private sealed record ReaderChapter(string Title, string[] Paragraphs);
        private sealed class Article(params IUiComponent[] content) : HtmlElement("article", content);
        private sealed class Footer(params IUiComponent[] content) : HtmlElement("footer", content);
        private sealed class Small(params IUiComponent[] content) : HtmlElement("small", content);
        private sealed class Aside(params IUiComponent[] content) : HtmlElement("aside", content);
    }
}
