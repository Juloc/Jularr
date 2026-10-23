using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The label is a UI text key or, for a number, the value itself; Scopes names (comma-separated) the media types a choice is for, for a script that hides it for others.
    public sealed record ChoicePick(string Value, bool Checked, string? LabelKey = null, string? Scopes = null, string? Label = null);

    // Pills show their state in colour; Checks are native checkboxes in two columns; Section frames checks with an icon and a count.
    public enum ChoiceLayout { Pills, Checks, Section }

    // Minor groups sit under another one with a smaller label; CountKey is a text with {0} of {1} chosen; Attributes mark inputs for a script that keeps its own state.
    public sealed record ChoiceOptions(bool Multiple = false, ChoiceLayout Layout = ChoiceLayout.Pills, string? Icon = null, string? CountKey = null, bool Minor = false,
        IReadOnlyList<IUiComponent>? Aside = null, IReadOnlyDictionary<string, string>? Attributes = null);

    // A named group of choices: radio buttons for one, checkboxes for several. Native inputs hold the state,
    // so a surrounding GET form submits them as they are.
    public sealed class ChoiceGroup(FrontendAssets assets, string labelKey, string name, IReadOnlyList<ChoicePick> picks, ChoiceOptions? options = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var settings = options ?? new ChoiceOptions();
            var id = "choice-" + name;
            var items = picks.Select(pick =>
            {
                var input = new Input().Attribute("type", settings.Multiple ? "checkbox" : "radio").Attribute("name", name).Attribute("value", pick.Value);
                if (pick.Checked) input.Attribute("checked", "checked");
                if (pick.Scopes is not null) input.Attribute("data-scopes", pick.Scopes);
                foreach (var (attribute, value) in settings.Attributes ?? new Dictionary<string, string>()) input.Attribute(attribute, value);
                return (IUiComponent)new Label(input, new Span(pick.LabelKey is null ? new ValueText(pick.Label ?? pick.Value) : new Text(pick.LabelKey)))
                    .Class(settings.Layout == ChoiceLayout.Pills ? "choice-group__item" : "choice-group__check");
            }).ToArray();

            var label = new List<IUiComponent>();
            if (settings.Icon is not null) label.Add(new Icon(settings.Icon, assets, 18));
            label.Add(new Span(new Text(labelKey)).Attribute("id", id + "-label"));
            var title = new List<IUiComponent> { new Div([.. label]).Class("choice-group__label") };
            if (settings.CountKey is not null)
            {
                title.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts[settings.CountKey], picks.Count(pick => pick.Checked), picks.Count))).Class("choice-group__count")
                    .Attribute("data-count-label", texts[settings.CountKey]));
            }

            var heading = new List<IUiComponent> { new Div([.. title]).Class("choice-group__title") };
            heading.AddRange(settings.Aside ?? []);
            var modifier = (settings.Layout == ChoiceLayout.Pills ? "" : settings.Layout == ChoiceLayout.Checks ? " choice-group--checks" : " choice-group--section") + (settings.Minor ? " choice-group--minor" : "");
            new Div(new Div([.. heading]).Class("choice-group__heading"), new Div(items).Class("choice-group__items")).Class("choice-group" + modifier)
                .Attribute("role", settings.Multiple ? "group" : "radiogroup").Attribute("aria-labelledby", id + "-label").WriteTo(writer, encoder, texts);
        }

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
