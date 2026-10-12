using System.Text.Encodings.Web;

using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared instance branding.
    public sealed class Brand(FrontendAssets assets, bool compact = false, bool login = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Share both brand marks.
            var glyph = new Span(
                Mark("brand-clean", "290 255 730 765", "brand-symbol__clean"),
                Mark("brand-original", "48 16 864 864", "brand-symbol__original"))
                .Class(login ? "brand-symbol auth-symbol" : "brand-symbol")
                .Attribute("aria-hidden", "true");
            var name = new Span(new ValueText(texts.InstanceName));
            name.Class(login ? "shell-brand__name auth-brand-name" : "shell-brand__name");

            var content = new List<IUiComponent> { glyph, name };
            if (!string.Equals(texts.InstanceName, "Jularr", StringComparison.Ordinal))
            {
                content.Add(new Span(new Text("app.byJularr")).Class("shell-brand__attribution"));
            }

            var brand = new Div(content.ToArray()).Class(login ? "auth-brand" : "shell-brand");
            if (compact) brand.Class("shell-brand shell-brand--compact");
            brand.Attribute("aria-label", texts.InstanceName);

            if (login)
            {
                brand.WriteTo(writer, encoder, texts);
            }
            else
            {
                new A(brand).Class(compact ? "app-header-brand" : "sidebar-brand")
                    .Attribute("href", "/").WriteTo(writer, encoder, texts);
            }
        }

        private IUiComponent Mark(string name, string viewBox, string cssClass) =>
            new Svg(new Use().Attribute("href", assets.IconHref(name))).Class(cssClass).Attribute("viewBox", viewBox)
                .Attribute("width", "48").Attribute("height", "48").Attribute("focusable", "false");

        private sealed class Svg(params IUiComponent[] content) : HtmlElement("svg", content);
        private sealed class Use() : HtmlElement("use");
    }
}
