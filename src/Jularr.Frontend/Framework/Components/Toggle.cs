using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed class Toggle : IUiComponent
    {
        private readonly IUiComponent _label;
        private readonly bool _hidesLabel;
        internal Input Control { get; } = new();

        public Toggle(string labelKey, bool isChecked = false, bool disabled = false, bool hidesLabel = false) : this(new Text(labelKey), isChecked, disabled, hidesLabel)
        {
        }

        // The label stays the switch's name for assistive technology; a page or row that shows the name itself hides it.
        public Toggle(IUiComponent label, bool isChecked = false, bool disabled = false, bool hidesLabel = false)
        {
            _label = label;
            _hidesLabel = hidesLabel;
            Control.Attribute("type", "checkbox").Attribute("role", "switch").Attribute("value", "true");
            if (isChecked) Control.Attribute("checked", "checked");
            if (disabled) Control.Attribute("disabled", "disabled");
        }

        // The switch sends its value under a name when it is on (the keys of the switched-on rows of a list).
        public Toggle Named(string name, string value)
        {
            Control.Attribute("name", name).Attribute("value", value);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Label(Control,
                new Span().Class("toggle__track").Attribute("aria-hidden", "true"),
                new Span(_label).Class(_hidesLabel ? "toggle__label visually-hidden" : "toggle__label")).Class("toggle").WriteTo(writer, encoder, texts);
        }

        private sealed class Label(params IUiComponent[] content) : HtmlElement("label", content);
    }
}
