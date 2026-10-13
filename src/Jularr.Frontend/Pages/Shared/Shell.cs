using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared application shell.
    public sealed class Shell(string pageTitleKey, string currentPath, ShellNavigation navigation, FrontendAssets assets, string? searchQuery = null, string? searchType = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Add available destinations.
            var primary = navigation.Primary.ToList();
            var secondary = navigation.Secondary.ToList();
            var mobile = navigation.Mobile.ToList();

            if (texts.Capabilities?.CanReadMedia is true)
            {
                var library = new ShellLink("nav.library", "/Library", "library");
                primary.Add(library);
                mobile.Insert(1, library);
                var watchlist = new ShellLink("watchlist.title", "/Watchlist", "home");
                primary.Add(watchlist);
                mobile.Add(watchlist);
            }

            if (texts.Capabilities?.CanManageMedia is true)
            {
                secondary.Add(new ShellLink("admin.works.title", "/Admin/Works", "settings"));
                secondary.Add(new ShellLink("admin.library.title", "/Admin/Library", "library"));
                secondary.Add(new ShellLink("admin.activity.title", "/Admin/Activity", "settings"));
            }

            if (texts.Capabilities?.CanRequest is true)
            {
                primary.Add(new ShellLink("requests.title", "/Requests", "home"));
            }

            if (texts.Capabilities?.CanManageRequests is true)
            {
                secondary.Add(new ShellLink("admin.requests.title", "/Admin/Requests", "settings"));
            }

            if (texts.Capabilities?.CanManageWanted is true)
            {
                secondary.Add(new ShellLink("admin.wanted.title", "/Admin/Wanted", "settings"));
            }

            // Render shared navigation.
            // The one global search sits in the header.
            var header = new List<IUiComponent>
            {
                new Brand(assets, compact: true),
                new Nav(new Span(new Text(pageTitleKey)).Attribute("aria-current", "page")).Class("app-header-location").Attribute("aria-label", texts["nav.breadcrumb"])
            };
            if (texts.Capabilities?.CanReadMedia is true)
            {
                header.Add(new SearchBox("/", "search.global", searchQuery, searchType is null ? null : new Dictionary<string, string> { ["type"] = searchType }));
            }

            new Header([.. header]).Class("app-header").WriteTo(writer, encoder, texts);

            new Aside(
                new Brand(assets),
                Links(primary, "nav.primary", "sidebar-navigation", false, texts),
                Links(secondary, "nav.secondary", "sidebar-navigation sidebar-secondary", false, texts)
            ).Class("app-sidebar").WriteTo(writer, encoder, texts);

            Links(mobile, "nav.mobile", "mobile-navigation", true, texts)
                .WriteTo(writer, encoder, texts);
        }

        private IUiComponent Links(IReadOnlyList<ShellLink> links, string titleKey, string cssClass, bool mobile, UiText texts)
        {
            var children = links.Select(link =>
            {
                var active = link.Href == "/"
                    ? currentPath == "/"
                    : string.Equals(currentPath, link.Href, StringComparison.OrdinalIgnoreCase)
                        || currentPath.StartsWith(link.Href + "/", StringComparison.OrdinalIgnoreCase);

                var className = mobile ? "mobile-link" : "nav-link";
                var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                    .Class(active ? className + " is-active" : className)
                    .Attribute("href", link.Href);
                if (active) anchor.Attribute("aria-current", "page");
                return (IUiComponent)anchor;
            }).ToArray();

            return new Nav(children).Class(cssClass)
                .Attribute("aria-label", texts[titleKey]);
        }

        private sealed class Aside(params IUiComponent[] children) : HtmlElement("aside", children);
    }

    public sealed record ShellLink(string Key, string Href, string Icon);

    // Required destinations; Services determine optional links.
    public sealed class ShellNavigation
    {
        public IReadOnlyList<ShellLink> Primary { get; } =
        [
            new("nav.home", "/", "home")
        ];

        public IReadOnlyList<ShellLink> Secondary { get; } =
        [
            new("nav.settings", "/Settings", "settings")
        ];

        public IReadOnlyList<ShellLink> Mobile { get; } =
        [
            new("nav.home", "/", "home"),
            new("nav.settings", "/Settings", "settings")
        ];
    }
}
