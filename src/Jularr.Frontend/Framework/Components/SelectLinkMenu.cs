using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A menu of links that shows the current choice on its button; native details, closed by an outside click or Escape (SelectLinkMenu.entry.ts).
    public sealed class SelectLinkMenu(FrontendAssets assets, IReadOnlyList<NavigationTab> items, string ariaLabelKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var current = items.FirstOrDefault(item => item.Selected) ?? items[0];
            var links = items.Select(item =>
            {
                var link = new A(new Text(item.LabelKey)).Attribute("href", item.Href).Class(item.Selected ? "select-link-menu__item select-link-menu__item--current" : "select-link-menu__item");
                return (IUiComponent)(item.Selected ? link.Attribute("aria-current", "true") : link);
            });

            new Details(new Summary(new Span(new Text(current.LabelKey)), new Icon("chevron-down", assets, 16)).Class("select-link-menu__button"),
                new Nav([.. links]).Class("select-link-menu__list").Attribute("aria-label", texts[ariaLabelKey])).Class("select-link-menu").WriteTo(writer, encoder, texts);
        }
    }
}
