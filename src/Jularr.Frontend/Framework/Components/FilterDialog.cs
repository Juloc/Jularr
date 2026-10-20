using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record FilterChoice(string Name, string Value, IUiComponent Label, bool Checked, bool Radio);

    public sealed record FilterGroup(IUiComponent? Legend, IReadOnlyList<FilterChoice> Choices);

    // A native modal around a GET form of checkboxes and radios; the opener is any element with data-filter-open="<id>" (FilterDialog.entry.ts).
    // Values the form does not edit travel as hidden fields.
    public sealed class FilterDialog(FrontendAssets assets, string id, string titleKey, string action, IReadOnlyDictionary<string, string> keep, IReadOnlyList<FilterGroup> groups, string resetHref) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var body = new List<IUiComponent>();
            foreach (var group in groups)
            {
                var choices = new List<IUiComponent>();
                if (group.Legend is { } legend) choices.Add(new Legend(legend));
                choices.Add(new Div([.. group.Choices.Select(Choice)]).Class("filter-dialog__choices"));
                body.Add(new Fieldset([.. choices]).Class("filter-dialog__group"));
            }

            var hidden = keep.Select(pair => (IUiComponent)new Input().Attribute("type", "hidden").Attribute("name", pair.Key).Attribute("value", pair.Value));
            var form = new FormElement(
                new Div(new H2(new Text(titleKey)).Attribute("id", id + "-title"), new Button(new Icon("close", assets, 18)).IconOnly(texts["common.close"]).Attribute("data-filter-close", "")).Class("filter-dialog__head"),
                new Div([.. hidden, .. body]).Class("filter-dialog__body"),
                new Div(new A(new Text("calendar.reset")).Class("button").Attribute("href", resetHref), new Button(new Text("calendar.apply")).Primary().Submit()).Class("filter-dialog__foot"))
                .Attribute("method", "get").Attribute("action", action);
            new Dialog(form).Class("filter-dialog").Attribute("id", id).Attribute("aria-labelledby", id + "-title").WriteTo(writer, encoder, texts);
        }

        private static IUiComponent Choice(FilterChoice choice)
        {
            var input = new Input().Attribute("type", choice.Radio ? "radio" : "checkbox").Attribute("name", choice.Name).Attribute("value", choice.Value).Class("filter-dialog__input");
            return new Label(choice.Checked ? input.Attribute("checked", "checked") : input, new Span(choice.Label)).Class("filter-dialog__choice");
        }

        private sealed class Dialog(params IUiComponent[] children) : HtmlElement("dialog", children);

        private sealed class FormElement(params IUiComponent[] children) : HtmlElement("form", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);

        private sealed class Fieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);

        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);
    }
}
