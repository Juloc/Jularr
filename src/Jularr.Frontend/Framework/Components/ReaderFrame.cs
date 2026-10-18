namespace Jularr.Frontend.Framework
{
    // One frame; the owning page supplies authorized content and available tools.
    public sealed class ReaderFrame(FrontendAssets assets, UiText texts, IUiComponent top, IUiComponent contents, IUiComponent content, IUiComponent bottom, params IUiComponent[] panels) : HtmlElement("section",
        top,
        new Aside(contents).Class("reader__sidebar"),
        new Div(new Div().Class("reader__stack reader__stack--left").Attribute("aria-hidden", "true"),
            new Div().Class("reader__stack reader__stack--right").Attribute("aria-hidden", "true"),
            new Div(content).Class("reader__paper"),
            new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.previousPage"]).Attribute("data-turn", "-1").Class("button button--icon reader__edge reader__edge--previous"),
            new Button(new Icon("chevron-left", assets)).IconOnly(texts["reader.nextPage"]).Attribute("data-turn", "1").Class("button button--icon reader__edge reader__edge--next"))
                .Class("reader__canvas").Attribute("tabindex", "0").Attribute("role", "region").Attribute("aria-labelledby", "reader-document-title"),
        bottom,
        new Div(panels).Class("reader__panels"),
        new P(new Text("reader.animationUnavailable")).Attribute("data-role", "animation-error").Attribute("role", "status").Attribute("hidden", "hidden"),
        new P(new Text("reader.fullscreenUnavailable")).Attribute("data-role", "fullscreen-error").Attribute("role", "status").Attribute("hidden", "hidden"))
    {
    }
}
