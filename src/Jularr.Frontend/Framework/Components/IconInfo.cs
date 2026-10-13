using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Hover, focus and native disclosure.
    public sealed class IconInfo(IUiComponent icon, string label) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Details(
                new Summary(icon).Class("icon-info__trigger").Attribute("aria-label", label),
                new Span(new ValueText(label)).Class("icon-info__label")
            ).Class("icon-info").WriteTo(writer, encoder, texts);
        }

        private sealed class Details(params IUiComponent[] content) : HtmlElement("details", content);
        private sealed class Summary(params IUiComponent[] content) : HtmlElement("summary", content);
    }
}
