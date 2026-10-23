namespace Jularr.Frontend.Framework
{
    // One frame; the owning page supplies authorized content and available tools.
    // The bottom bar holds the progress slider on its upper edge, then contents and context, the page controls and the view tools.
    public sealed class ReaderFrame(FrontendAssets assets, UiText texts, IUiComponent top, IUiComponent contents, IUiComponent content, IUiComponent bottom, params IUiComponent[] panels) : HtmlElement("section",
        top,
        new Aside(
            new Div(new H2(new Text("reader.contents")),
                new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-reader-close", "contents"))
                    .Class("reader__panel-heading"), contents).Class("reader__sidebar"),
        new Div(new Div(content).Class("reader__paper"),
            new ReaderBook().Class("reader-book").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"))
                .Class("reader__canvas").Attribute("tabindex", "0").Attribute("role", "region").Attribute("aria-labelledby", "reader-document-title"),
        new Div(
            new Div(new Button(new Icon("player-list", assets)).IconOnly(texts["reader.contents"])
                    .Class("button button--icon reader__contents-tool").Attribute("data-action", "contents").Attribute("aria-expanded", "false"),
                bottom).Class("reader__footer-start"),
            new Nav(
                new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.previousPage"]).Attribute("data-turn", "-1"),
                new Span().Attribute("data-role", "pages").Attribute("aria-live", "polite"),
                new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.nextPage"]).Attribute("data-turn", "1").Class("button button--icon reader__turn-next"))
                .Class("reader__page-controls").Attribute("data-role", "turn-tools").Attribute("hidden", "hidden").Attribute("aria-label", texts["reader.pageNavigation"]),
            new Nav(
                new Button(new Icon("player-theater", assets)).IconOnly(texts["reader.theater"])
                    .Attribute("data-reader-theater", "").Attribute("aria-pressed", "false").Attribute("data-overflow", "theater").Attribute("data-overflow-rank", "1"),
                new Button(new Icon("player-fullscreen", assets)).IconOnly(texts["player.fullscreen"])
                    .Attribute("data-reader-fullscreen", "").Attribute("aria-pressed", "false").Attribute("data-overflow", "fullscreen").Attribute("data-overflow-rank", "2"),
                new Button(new Icon("settings", assets)).IconOnly(texts["reader.settings"])
                    .Attribute("data-action", "settings").Attribute("aria-expanded", "false"))
                .Class("reader__view-tools").Attribute("aria-label", texts["reader.presentation"]))
            .Class("reader__footer"),
        new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.showControls"])
            .Class("button button--icon reader__reveal reader__reveal--top").Attribute("data-reader-reveal", "").Attribute("aria-expanded", "false").Attribute("hidden", "hidden")
            .TextAttribute("data-show-label", "reader.showControls").TextAttribute("data-hide-label", "reader.hideControls"),
        new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.showControls"])
            .Class("button button--icon reader__reveal reader__reveal--bottom").Attribute("data-reader-reveal", "").Attribute("aria-expanded", "false").Attribute("hidden", "hidden")
            .TextAttribute("data-show-label", "reader.showControls").TextAttribute("data-hide-label", "reader.hideControls"),
        new Div(panels).Class("reader__panels"),
        new P(new Text("reader.animationUnavailable")).Attribute("data-role", "animation-error").Attribute("role", "status").Attribute("hidden", "hidden"),
        new P(new Text("reader.fullscreenUnavailable")).Attribute("data-role", "fullscreen-error").Attribute("role", "status").Attribute("hidden", "hidden"))
    {
    }
}
