using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One line of data text that ends in an ellipsis where it has no room; then hover, focus and touch show it whole.
    public sealed class Truncated(string value) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Span(new Span(new ValueText(value)).Class("truncated__text"), new Span(new ValueText(value)).Class("truncated__tip").Attribute("aria-hidden", "true"))
                .Class("truncated").WriteTo(writer, encoder, texts);
    }
}
