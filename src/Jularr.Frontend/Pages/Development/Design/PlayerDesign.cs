using System.Globalization;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Development.Design
{
    public sealed partial class DesignModel
    {
        private void DefinePlayer(Page page)
        {
            page.Asset("Pages/Development/Design/PlayerDesign.entry.ts");
            page.Asset("Pages/Development/Design/PlayerDesign.entry.scss");

            // Explicit sample, no sessions.
            var image = assets.ImageUrl("Pages/Development/Design/Images/your-name-backdrop.jpg");
            var source = new PlayerSource(texts["design.player.work"], "S1 · E4", "/Design?pane=player", "", "", "", image,
                [new("0", "English · Stereo", "en", true, true), new("1", "Deutsch · 5.1", "de", false, true)],
                [new("forced", "English · Forced", "en", true, true), new("english", "English", "en", false, true), new("german", "Deutsch", "de", false, true)],
                PreviousHref: "#episode-3", NextHref: "#episode-5");

            // Illustrative episode selection.
            var episodes = Enumerable.Range(1, 6).Select(number => (IUiComponent)new Button(
                new MediaImage(image, "", 160, 90),
                new Div(new Span(new ValueText("E" + number.ToString(CultureInfo.InvariantCulture))),
                    new Span(new Text("design.player.episode" + number.ToString(CultureInfo.InvariantCulture))), new Small(new ValueText("24:10"))),
                new Icon(number < 4 ? "check" : "player-play", assets))
                .Attribute("data-episode", number.ToString(CultureInfo.InvariantCulture))
                .Attribute("aria-pressed", number == 4 ? "true" : "false")
                .Attribute("id", "episode-" + number.ToString(CultureInfo.InvariantCulture))
                .Class("button player-workshop__episode")).ToArray();

            var chapterData = new[] { (Time: "00:00", Position: 0), (Time: "05:12", Position: 312), (Time: "12:34", Position: 754), (Time: "17:48", Position: 1068) };
            var chapters = chapterData.Select(chapter => (IUiComponent)new Button(
                new MediaImage(image, "", 240, 135), new Span(new Text("design.player.chapter")), new Small(new ValueText(chapter.Time)))
                .Attribute("data-chapter", chapter.Position.ToString(CultureInfo.InvariantCulture))
                .Class("button player-workshop__chapter")).ToArray();

            page.Content.Add(new Div(
                new P(new Text("design.player.previewNotice")).Class("player-workshop__notice"),
                new Div(
                    new Div(
                        new UiPlayer(source, assets).Settings(DefinePlayerSettings(source)),
                        new Div(new Span(new Text("design.player.captionPrimary")).Attribute("data-caption", "primary"),
                            new Span(new Text("design.player.captionSecondary")).Attribute("data-caption", "secondary").Attribute("hidden", "hidden"))
                            .Class("player-workshop__captions").Attribute("hidden", "hidden"),
                        new Div(chapterData.Select(chapter => (IUiComponent)new Button().Class("player-workshop__marker")
                            .Attribute("data-chapter", chapter.Position.ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["design.player.chapter"] + " " + chapter.Time)).ToArray())
                            .Class("player-workshop__markers").Attribute("data-timeline-markers", "").Attribute("hidden", "hidden"),
                        new Div(new MediaImage(image, "", 160, 90), new Span().Attribute("data-seek-time", ""))
                            .Attribute("data-seek-preview", "").Attribute("hidden", "hidden"),
                        new Button(new Icon("key", assets), new Text("design.player.unlock")).Attribute("data-action", "unlock-preview").Attribute("hidden", "hidden").Class("button player-workshop__unlock")
                    ).Class("player-workshop__stage"),
                    new Aside(
                        new Div(new NavigationTabs([new("design.player.episodes", "#player-episodes", true), new("media.details", "#player-details", false)], "nav.tabs"))
                            .Attribute("data-sidebar-tabs", ""),
                        new Div(
                            new Fieldset(new Legend(new Label(new Text("design.player.season")).Attribute("for", "preview-season")),
                                new Select(new Option(new ValueText("S1 · 6")).Attribute("value", "1"), new Option(new ValueText("S2 · 6")).Attribute("value", "2"))
                                    .Attribute("id", "preview-season").Attribute("data-sample-season", "")).Class("ui-field__control"),
                            new Div(episodes).Class("player-workshop__episodes")).Attribute("id", "player-episodes"),
                        new Div(new H2(new Text("design.player.work")), new P(new Text("design.player.synopsis")), new P(new ValueText("2026 · 24:10 · HD")),
                            new P(new Text("design.player.localOnly"))).Attribute("id", "player-details").Attribute("hidden", "hidden")
                    ).Class("player-workshop__sidebar")
                ).Class("player-workshop__layout"),
                new Details(new Summary(new Text("design.player.chapters")), new Div(chapters).Class("player-workshop__chapters"))
                    .Class("player-workshop__chapter-section"),
                new P(new Text("design.player.previewState")).Class("player-workshop__feedback").Attribute("role", "status").Attribute("data-demo-feedback", "")
            ).Class("player-workshop").Attribute("data-player-workshop", "").Attribute("id", "player-preview"));
        }

        private sealed class Small(params IUiComponent[] children) : HtmlElement("small", children);
        private sealed class Aside(params IUiComponent[] children) : HtmlElement("aside", children);
    }
}
