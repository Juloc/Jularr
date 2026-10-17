using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Fixed colors, shared sprite.
    public sealed class Flag(string name, FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Svg(new Use().Attribute("href", assets.IconHref(name)))
                .Class("ui-flag")
                .Attribute("viewBox", "0 0 30 18")
                .Attribute("width", "20").Attribute("height", "12")
                .Attribute("fill", "#000").Attribute("stroke", "none")
                .Attribute("aria-hidden", "true").Attribute("focusable", "false")
                .WriteTo(writer, encoder, texts);
        }

        private sealed class Svg(params IUiComponent[] content) : HtmlElement("svg", content);
        private sealed class Use() : HtmlElement("use");
    }
}
