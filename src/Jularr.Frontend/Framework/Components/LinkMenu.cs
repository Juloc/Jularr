using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record LinkMenuItem(IUiComponent Label, string Href, bool Current = false);

    // A native disclosure: the summary opens a list of links (the current one marked); LinkMenu.entry.ts closes it
    // on an outside tap or Escape. Without script it stays a plain, keyboard-usable disclosure.
    public sealed class LinkMenu(IUiComponent summary, IReadOnlyList<LinkMenuItem> items) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var links = items.Select(item =>
            {
                var link = new A(item.Label).Class(item.Current ? "link-menu__link is-active" : "link-menu__link").Attribute("href", item.Href);
                return (IUiComponent)(item.Current ? link.Attribute("aria-current", "page") : link);
            });
            new Details(new Summary(summary).Class("link-menu__summary"), new Div([.. links]).Class("link-menu__list")).Class("link-menu").WriteTo(writer, encoder, texts);
        }
    }
}
