using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record ChoiceOption(string LabelKey, string Value, bool Selected);

    // One select in a GET form that reloads the page with the choice; other address values stay as hidden fields.
    // Script submits on change and hides the button, which keeps the select usable without it.
    public sealed class ChoiceSelect(string action, string name, string labelKey, IReadOnlyList<ChoiceOption> options, IReadOnlyDictionary<string, string>? keep = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var id = "choice-" + name;
            var parts = new List<IUiComponent>();
            foreach (var (field, value) in keep ?? new Dictionary<string, string>())
            {
                parts.Add(new Input().Attribute("type", "hidden").Attribute("name", field).Attribute("value", value));
            }

            parts.Add(new Label(new Text(labelKey)).Class("visually-hidden").Attribute("for", id));
            parts.Add(new Select([.. options.Select(option =>
            {
                var item = new Option(new Text(option.LabelKey)).Attribute("value", option.Value);
                return (IUiComponent)(option.Selected ? item.Attribute("selected", "selected") : item);
            })]).Class("choice-select__control").Attribute("id", id).Attribute("name", name));
            parts.Add(new Button(new Text("common.apply")).Submit().Class("button button--compact choice-select__apply"));
            new Form(parts.ToArray()).Class("choice-select").Attribute("action", action).Attribute("method", "get").WriteTo(writer, encoder, texts);
        }

        private sealed class Form(params IUiComponent[] children) : HtmlElement("form", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);

        private sealed class Select(params IUiComponent[] children) : HtmlElement("select", children);

        private sealed class Option(params IUiComponent[] children) : HtmlElement("option", children);
    }
}
