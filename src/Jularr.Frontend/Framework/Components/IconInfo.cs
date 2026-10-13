using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Hover, focus and native disclosure.
    public sealed class IconInfo(IUiComponent icon, string label) : IUiComponent
    {
        private string? _previewId;
        private string? _href;

        public IconInfo Preview(string id, string href)
        {
            _previewId = id;
            _href = href;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (_previewId is not null)
            {
                new Div(new A(icon).Class("icon-info__trigger").Attribute("href", _href!)
                    .Attribute("aria-label", label).Attribute("aria-haspopup", "dialog").Attribute("data-preview-open", _previewId))
                    .Class("icon-info").WriteTo(writer, encoder, texts);
                return;
            }

            new Details(
                new Summary(icon).Class("icon-info__trigger").Attribute("aria-label", label),
                new Span(new ValueText(label)).Class("icon-info__label")
            ).Class("icon-info").WriteTo(writer, encoder, texts);
        }

        private sealed class Details(params IUiComponent[] content) : HtmlElement("details", content);
        private sealed class Summary(params IUiComponent[] content) : HtmlElement("summary", content);
    }
}
