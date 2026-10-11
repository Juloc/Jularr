using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class ShellMobileNavigation(
    IReadOnlyList<ShellLink> links, string currentPath, FrontendAssets assets) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var content = links.Select(link =>
        {
            var active = link.Href == "/" ? currentPath == "/"
                : string.Equals(currentPath, link.Href, StringComparison.OrdinalIgnoreCase)
                    || currentPath.StartsWith(link.Href + "/", StringComparison.OrdinalIgnoreCase);

            var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                .Class(active ? "mobile-link is-active" : "mobile-link")
                .Attribute("href", link.Href);
            if (active) anchor.Attribute("aria-current", "page");
            return (IUiComponent)anchor;
        }).ToArray();

        new Nav(content).Class("mobile-navigation")
            .Attribute("aria-label", texts["nav.mobile"])
            .WriteTo(writer, encoder, texts);
    }
}
