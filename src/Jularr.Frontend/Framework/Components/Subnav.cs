using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed record UiNavItem(string Key, string Href);

public sealed class UiSubnav(IReadOnlyList<UiNavItem> items) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var links = items.Select(item =>
        {
            var link = new A(new Text(item.Key)).Attribute("href", item.Href);
            // Routing remains server-side; no JS navigation and no duplicate state.
            return (IUiComponent)link;
        }).ToArray();

        new Nav(links).Class("page-subnav")
            .Attribute("aria-label", texts["nav.primary"]).WriteTo(writer, encoder, texts);
    }
}
