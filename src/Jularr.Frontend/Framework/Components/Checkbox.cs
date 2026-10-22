using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A box or a round choice with its text, in the shared look (Checkbox.scss); the native input owns state, keyboard and the submitted value.
    public sealed class Checkbox : IUiComponent
    {
        private readonly IUiComponent _label;
        internal Input Control { get; } = new();

        public Checkbox(IUiComponent label, string? name = null, string? value = null, bool isChecked = false, bool disabled = false, bool radio = false)
        {
            _label = label;
            Control.Attribute("type", radio ? "radio" : "checkbox");
            if (name is not null) Control.Attribute("name", name);
            if (value is not null) Control.Attribute("value", value);
            if (isChecked) Control.Attribute("checked", "checked");
            if (disabled) Control.Attribute("disabled", "disabled");
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Label(Control, new Span(_label)).Class("ui-checkbox").WriteTo(writer, encoder, texts);

        private sealed class Label(params IUiComponent[] content) : HtmlElement("label", content);
    }
}
