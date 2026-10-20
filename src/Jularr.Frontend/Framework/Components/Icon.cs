using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Reuses the content-fingerprinted sprite; SVG never embeds service data.
    public sealed class Icon(string name, FrontendAssets assets, int size = 20, string stroke = "1.8") : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Svg(
                new Use().Attribute("href", assets.IconHref(name))
            ).Class("ui-icon")
                .Attribute("viewBox", "0 0 24 24")
                .Attribute("width", size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Attribute("height", size.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Attribute("fill", "none")
                .Attribute("stroke", "currentColor")
                .Attribute("stroke-width", stroke)
                .Attribute("stroke-linecap", "round")
                .Attribute("stroke-linejoin", "round")
                .Attribute("aria-hidden", "true")
                .Attribute("focusable", "false")
                .WriteTo(writer, encoder, texts);
        }
    }
}
