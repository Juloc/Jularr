using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A menu of links that shows the current choice on its button; native details, closed by an outside click or Escape (SelectLinkMenu.entry.ts).
    // An optional caption names what is chosen ahead of the choice (Version, Season); the modifier picks a look (compact, glass over artwork).
    public sealed class SelectLinkMenu(FrontendAssets assets, IReadOnlyList<NavigationTab> items, string ariaLabelKey, string? captionKey = null, string? modifier = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var current = items.FirstOrDefault(item => item.Selected) ?? items[0];
            var links = items.Select(item =>
            {
                var link = new A(item.Label()).Attribute("href", item.Href).Class(item.Selected ? "select-link-menu__item select-link-menu__item--current" : "select-link-menu__item");
                return (IUiComponent)(item.Selected ? link.Attribute("aria-current", "true") : link);
            });

            var button = new List<IUiComponent>();
            if (captionKey is not null) button.Add(new Span(new Text(captionKey)).Class("select-link-menu__caption"));
            button.Add(new Span(current.IsValue ? new Truncated(current.LabelKey) : current.Label()).Class("select-link-menu__value"));
            button.Add(new Icon("chevron-down", assets, 16));

            new Details(new Summary([.. button]).Class("select-link-menu__button"),
                new Nav([.. links]).Class("select-link-menu__list").Attribute("aria-label", texts[ariaLabelKey]))
                .Class(modifier is null ? "select-link-menu" : "select-link-menu select-link-menu--" + modifier).WriteTo(writer, encoder, texts);
        }
    }
}
