using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The label is a UI text key or, for a number, the value itself.
    public sealed record ChoicePick(string Value, bool Checked, string? LabelKey = null);

    // A named group of choices as pills: radio buttons for one, checkboxes for several. Native inputs hold the state,
    // so a surrounding GET form submits them as they are; Attributes mark inputs for a script that keeps its own state.
    public sealed class ChoiceGroup(string labelKey, string name, IReadOnlyList<ChoicePick> picks, bool multiple = false, IReadOnlyList<IUiComponent>? aside = null,
        IReadOnlyDictionary<string, string>? attributes = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var id = "choice-" + name;
            var items = picks.Select(pick =>
            {
                var input = new Input().Attribute("type", multiple ? "checkbox" : "radio").Attribute("name", name).Attribute("value", pick.Value);
                if (pick.Checked) input.Attribute("checked", "checked");
                foreach (var (attribute, value) in attributes ?? new Dictionary<string, string>()) input.Attribute(attribute, value);
                return (IUiComponent)new Label(input, new Span(pick.LabelKey is null ? new ValueText(pick.Value) : new Text(pick.LabelKey))).Class("choice-group__item");
            }).ToArray();

            var heading = new List<IUiComponent> { new Span(new Text(labelKey)).Class("choice-group__label").Attribute("id", id + "-label") };
            heading.AddRange(aside ?? []);
            new Div(new Div([.. heading]).Class("choice-group__heading"), new Div(items).Class("choice-group__items")).Class("choice-group")
                .Attribute("role", multiple ? "group" : "radiogroup").Attribute("aria-labelledby", id + "-label").WriteTo(writer, encoder, texts);
        }

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
