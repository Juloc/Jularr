using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record Chip(string LabelKey, string Href, bool Selected);

    // A master link selects the whole group; its chips toggle single members.
    public sealed record ChipGroup(string LabelKey, string Icon, string Href, bool AnySelected, bool Solo, string AriaLabel, IReadOnlyList<Chip> Chips);

    // A capsule of linked filter chips grouped by area, with an optional tool slot at its end. The address of each link already holds the resulting selection.
    public sealed class ChipBar(FrontendAssets assets, Chip all, IReadOnlyList<ChipGroup> groups, string ariaLabelKey, IReadOnlyList<IUiComponent>? tools = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var chips = new List<IUiComponent> { Link(new A(new Icon("grid", assets, 18), new Span(new Text(all.LabelKey))).Class("chip-bar__pill" + (all.Selected ? " chip-bar__pill--selected" : "")), all.Href, all.Selected) };
            foreach (var group in groups)
            {
                var master = new A(new Icon(group.Icon, assets, 18), new Span(new Text(group.LabelKey))).Class("chip-bar__master" + (group.AnySelected ? " chip-bar__master--selected" : "") + (group.Solo ? " chip-bar__master--solo" : ""))
                    .Attribute("aria-label", group.AriaLabel);
                chips.Add(new Div(Link(master, group.Href, group.Solo),
                    new Div([.. group.Chips.Select(chip => Link(new A(new Text(chip.LabelKey)).Class("chip-bar__chip" + (chip.Selected ? " chip-bar__chip--selected" : "")), chip.Href, chip.Selected))]).Class("chip-bar__inner"))
                    .Class("chip-bar__group"));
            }

            var children = new List<IUiComponent> { new Div([.. chips]).Class("chip-bar__chips") };
            if (tools is { Count: > 0 }) children.Add(new Div([.. tools]).Class("chip-bar__tools"));
            new Div([.. children]).Class("chip-bar").Attribute("role", "group").Attribute("aria-label", texts[ariaLabelKey]).WriteTo(writer, encoder, texts);
        }

        private static IUiComponent Link(HtmlElement link, string href, bool current)
        {
            link.Attribute("href", href);
            return current ? link.Attribute("aria-current", "true") : link;
        }
    }
}
