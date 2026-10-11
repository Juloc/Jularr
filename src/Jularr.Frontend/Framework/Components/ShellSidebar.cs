using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class ShellSidebar(
    IReadOnlyList<ShellLink> primary, IReadOnlyList<ShellLink> secondary,
    string currentPath, FrontendAssets assets) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Aside(
            new Brand(),
            RenderNavigation(primary, "nav.primary", "sidebar-navigation", texts),
            RenderNavigation(secondary, "nav.secondary", "sidebar-navigation sidebar-secondary", texts)
        ).Class("app-sidebar").WriteTo(writer, encoder, texts);
    }

    private IUiComponent RenderNavigation(IReadOnlyList<ShellLink> links, string ariaKey, string classes, UiText texts) =>
        new Nav(links.Select(link =>
        {
            var active = IsActive(link);
            var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                .Class(active ? "nav-link is-active" : "nav-link")
                .Attribute("href", link.Href);
            if (active) anchor.Attribute("aria-current", "page");
            return (IUiComponent)anchor;
        }).ToArray()).Class(classes).Attribute("aria-label", texts[ariaKey]);

    private bool IsActive(ShellLink link) =>
        link.Href == "/" ? currentPath == "/" :
        string.Equals(currentPath, link.Href, StringComparison.OrdinalIgnoreCase)
        || currentPath.StartsWith(link.Href + "/", StringComparison.OrdinalIgnoreCase);

    private sealed class Aside(params IUiComponent[] content) : HtmlElement("aside", content);
}
