using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Reuses the content-fingerprinted sprite; SVG never embeds service data.
    public sealed class Icon(string name, FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Svg(
                new Use().Attribute("href", assets.IconHref(name))
            ).Class("ui-icon")
                .Attribute("viewBox", "0 0 24 24")
                .Attribute("width", "20").Attribute("height", "20")
                .Attribute("fill", "none")
                .Attribute("stroke", "currentColor")
                .Attribute("stroke-width", "1.8")
                .Attribute("stroke-linecap", "round")
                .Attribute("stroke-linejoin", "round")
                .Attribute("aria-hidden", "true")
                .Attribute("focusable", "false")
                .WriteTo(writer, encoder, texts);
        }

        private sealed class Svg(params IUiComponent[] content) : HtmlElement("svg", content);
        private sealed class Use() : HtmlElement("use");
    }
}
