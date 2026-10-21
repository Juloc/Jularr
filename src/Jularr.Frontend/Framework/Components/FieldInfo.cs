using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A form field's example and explanation, named by a page instead of the field's own; an empty key shows none, such as on a repeated password.
    // Aside sits at the end of the label's row (a "Forgot password?" link), Trailing at the end of the field itself (a show-password button).
    public sealed record FieldHelp(string? Placeholder = null, string? Info = null, IUiComponent? Aside = null, IUiComponent? Trailing = null);

    // The (i) beside a label: its explanation shows on hover, keyboard focus and a tap; nothing when the field has none.
    public sealed class FieldInfo(string key, string id) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!texts.Has(key))
            {
                return;
            }

            new Span(
                new Button(new ValueText("i")).Class("ui-field__info").Attribute("type", "button").Attribute("aria-label", texts["form.info"]).Attribute("aria-describedby", id),
                new Span(new Text(key)).Class("ui-field__tip").Attribute("id", id).Attribute("role", "tooltip")
            ).Class("ui-field__help").WriteTo(writer, encoder, texts);
        }
    }
}
