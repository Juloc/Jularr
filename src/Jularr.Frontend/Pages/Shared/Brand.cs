using System.Text.Encodings.Web;

using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared;

// Shared visual mark. Full pages decide where this small component belongs.
public sealed class Brand(bool compact = false, bool login = false) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var glyph = new Span(new Mark()).Class(login ? "brand-symbol auth-symbol" : "brand-symbol")
            .Attribute("aria-hidden", "true");
        var name = new Span(new Text("app.name"));
        if (login) name.Class("auth-brand-name");

        var brand = new Div(glyph, name).Class(login ? "auth-brand" : "shell-brand");
        if (compact) brand.Class("shell-brand shell-brand--compact");
        brand.Attribute("aria-label", texts["app.name"]);

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

    private sealed class Mark : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            encoder.Encode(writer, "J"); // Brand mark, not translatable UI copy.
    }
}
