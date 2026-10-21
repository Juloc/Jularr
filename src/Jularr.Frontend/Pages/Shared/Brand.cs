using System.Text.Encodings.Web;

using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared instance branding.
    public sealed class Brand(FrontendAssets assets, bool compact = false, bool login = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // One single-colour mark in the text colour, in both skins and brightnesses.
            var glyph = new Span(Mark())
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

        private IUiComponent Mark()
        {
            // Symbol owns logo scaling.
            return new Svg(new Use().Attribute("href", assets.IconHref("brand-mark")).Attribute("width", "48").Attribute("height", "48"))
                .Class("brand-symbol__mark").Attribute("viewBox", "0 0 48 48")
                .Attribute("width", "48").Attribute("height", "48").Attribute("focusable", "false");
        }
    }
}
