using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One way a list can be shown: its name (the tooltip and the label), the address that shows it, its icon and whether it is the one in use.
    public sealed record ViewChoice(string LabelKey, string Href, IUiComponent Icon, bool Selected);

    // The one switch between the views of a list (posters, overview, table): icon links in a small capsule, the chosen one marked. The page keeps the choice in this browser.
    public sealed class ViewSwitch(string ariaLabelKey, IReadOnlyList<ViewChoice> choices) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var links = choices.Select(choice =>
            {
                var link = new A(choice.Icon).Class(choice.Selected ? "view-switch__link view-switch__link--active" : "view-switch__link").Attribute("href", choice.Href)
                    .Attribute("aria-label", texts[choice.LabelKey]).Attribute("title", texts[choice.LabelKey]);
                return (IUiComponent)(choice.Selected ? link.Attribute("aria-current", "true") : link);
            }).ToArray();
            new Nav(links).Class("view-switch").Attribute("aria-label", texts[ariaLabelKey]).WriteTo(writer, encoder, texts);
        }
    }
}
