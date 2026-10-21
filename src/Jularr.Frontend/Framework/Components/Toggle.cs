using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed class Toggle : IUiComponent
    {
        private readonly string _labelKey;
        private readonly bool _hidesLabel;
        internal Input Control { get; } = new();

        // The label stays the switch's name for assistive technology; a page that shows the name itself hides it.
        public Toggle(string labelKey, bool isChecked = false, bool disabled = false, bool hidesLabel = false)
        {
            _labelKey = labelKey;
            _hidesLabel = hidesLabel;
            Control.Attribute("type", "checkbox").Attribute("role", "switch").Attribute("value", "true");
            if (isChecked) Control.Attribute("checked", "checked");
            if (disabled) Control.Attribute("disabled", "disabled");
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Label(Control,
                new Span().Class("toggle__track").Attribute("aria-hidden", "true"),
                new Span(new Text(_labelKey)).Class(_hidesLabel ? "toggle__label visually-hidden" : "toggle__label")).Class("toggle").WriteTo(writer, encoder, texts);
        }

        private sealed class Label(params IUiComponent[] content) : HtmlElement("label", content);
    }
}
