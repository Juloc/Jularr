using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed class Toggle : IUiComponent
    {
        private readonly string _labelKey;
        internal Input Control { get; } = new();

        public Toggle(string labelKey, bool isChecked = false, bool disabled = false)
        {
            _labelKey = labelKey;
            Control.Attribute("type", "checkbox").Attribute("role", "switch").Attribute("value", "true");
            if (isChecked) Control.Attribute("checked", "checked");
            if (disabled) Control.Attribute("disabled", "disabled");
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Label(Control,
                new Span().Class("toggle__track").Attribute("aria-hidden", "true"),
                new Span(new Text(_labelKey)).Class("toggle__label")).Class("toggle").WriteTo(writer, encoder, texts);
        }

        private sealed class Label(params IUiComponent[] content) : HtmlElement("label", content);
    }
}
