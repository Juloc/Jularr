using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared application shell.
    public sealed class Shell(string pageTitleKey, string currentPath, ShellNavigation navigation, FrontendAssets assets, string? searchQuery = null, string? searchType = null,
        IReadOnlyList<(string Key, string Href)>? parents = null, string? currentLabel = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Add available destinations.
            var primary = navigation.Primary.ToList();
            var secondary = navigation.Secondary.ToList();
            var mobile = navigation.Mobile.ToList();

            if (texts.CanReadMedia())
            {
                var library = new ShellLink("nav.library", "/Library", "library");
                primary.Add(library);
                mobile.Insert(1, library);
                if (texts.CanSeeCalendar())
                {
                    primary.Add(new ShellLink("calendar.title", "/Calendar", "calendar"));
                }

                if (texts.CanSeeStatistics())
                {
                    secondary.Add(new ShellLink("statistics.title", "/Statistics", "activity"));
                }

                var favorites = new ShellLink("favorites.title", "/Favorites", "heart");
                primary.Add(favorites);
                mobile.Add(favorites);
            }

            if (texts.CanAdminister())
            {
                secondary.Add(Group("nav.group.system", "server", new("system.title", "/Admin/System", ""), new("admin.transcoding.title", "/Admin/Transcoding", "")));
            }

            // Administration areas open into their pages.
            if (texts.CanManageMedia())
            {
                secondary.Add(Group("nav.group.media", "settings",
                    new("admin.works.title", "/Admin/Works", ""), new("admin.matches.title", "/Admin/Works/Matches", ""), new("admin.activity.title", "/Admin/Activity", ""),
                    new("admin.manualImport.title", "/Admin/ManualImport", ""),
                    new("admin.history.title", "/Admin/History", "")));
                secondary.Add(Group("nav.group.library", "library", [new("library.tab.Overview", "/Admin/Library", ""), new("library.tab.Unmatched", "/Admin/Library/Unmatched", ""),
                    new("library.tab.Files", "/Admin/Library/Files", ""),
                    .. texts.CanManageStorage() ? new ShellLink[] { new("library.tab.Naming", "/Admin/Library/Naming", ""), new("library.tab.Rename", "/Admin/Library/Rename", ""),
                        new("library.tab.Versions", "/Admin/Library/Versions", "") } : []]));
            }

            if (texts.CanRequest())
            {
                primary.Add(new ShellLink("requests.title", "/Requests", "home"));
            }

            if (texts.CanManageRequests())
            {
                secondary.Add(Group("nav.group.requests", "settings", [new("admin.requests.title", "/Admin/Requests", ""),
                    .. texts.CanReadRequestRules() ? new ShellLink[] { new("admin.rules.title", "/Admin/Rules", ""), new("admin.users.title", "/Admin/Requests/Users", "") } : []]));
            }

            var acquisition = new List<ShellLink>();
            if (texts.CanManageWanted()) acquisition.Add(new("admin.wanted.title", "/Admin/Wanted", ""));
            if (texts.CanConfigureAcquisition())
            {
                acquisition.AddRange([new("acquisition.role.Search", "/Admin/Acquisition", ""), new("acquisition.role.Download", "/Admin/Acquisition/Downloads", ""),
                    new("acquisition.role.Subtitles", "/Admin/Acquisition/Subtitles", ""), new("admin.profiles.title", "/Admin/Acquisition/Profiles", ""),
                    new("admin.sources.title", "/Admin/Acquisition/Sources", ""), new("admin.takeover.title", "/Admin/Acquisition/Takeover", ""), new("admin.browser.title", "/Admin/Browser", "")]);
            }

            if (acquisition.Count > 0) secondary.Add(Group("nav.group.acquisition", "settings", [.. acquisition]));

            // Render shared navigation.
            // The one global search sits in the header.
            var header = new List<IUiComponent>
            {
                new Brand(assets, compact: true),
                new Nav([.. (parents ?? []).SelectMany(parent => new IUiComponent[] { new A(new Text(parent.Key)).Attribute("href", parent.Href), new Span(new ValueText("›")).Class("app-header-separator").Attribute("aria-hidden", "true") }),
                    new Span(parents is { Count: > 0 } && currentLabel is not null ? new ValueText(currentLabel) : new Text(pageTitleKey)).Attribute("aria-current", "page")]).Class("app-header-location").Attribute("aria-label", texts["nav.breadcrumb"])
            };
            if (texts.CanReadMedia())
            {
                header.Add(new SearchBox("/", "search.global", searchQuery, searchType is null ? null : new Dictionary<string, string> { ["type"] = searchType }));
            }

            // Inbox with its unread count.
            if (texts.CanNotify())
            {
                var unread = texts.UnreadNotifications;
                header.Add(new A([new Icon("bell", assets), .. unread > 0 ? new IUiComponent[] { new Span(new ValueText(unread > 99 ? "99+" : unread.ToString(System.Globalization.CultureInfo.CurrentCulture))).Class("app-notifications__count") } : []])
                    .Class("app-notifications" + (currentPath.StartsWith("/Notifications", StringComparison.OrdinalIgnoreCase) ? " is-active" : ""))
                    .Attribute("href", "/Notifications")
                    .Attribute("aria-label", unread > 0 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, texts["notifications.unread"], unread) : texts["notifications.title"]));
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
                var className = mobile ? "mobile-link" : "nav-link";
                if (link.Children is { } pages)
                {
                    // The area of the current page stays open and marks it.
                    var current = Current(pages);
                    var group = new Details(new Summary(new Icon(link.Icon, assets), new Span(new Text(link.Key))).Class(current is null ? className : className + " is-active"),
                        new Div([.. pages.Select(page =>
                        {
                            var anchor = new A(new Span(new Text(page.Key))).Class(page == current ? "nav-link nav-link--sub is-active" : "nav-link nav-link--sub").Attribute("href", page.Href);
                            return page == current ? anchor.Attribute("aria-current", "page") : anchor;
                        })]).Class("nav-group__pages")).Class("nav-group");
                    return current is null ? group : group.Attribute("open", "open");
                }

                var active = Matches(link);
                var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                    .Class(active ? className + " is-active" : className)
                    .Attribute("href", link.Href);
                if (active) anchor.Attribute("aria-current", "page");
                return (IUiComponent)anchor;
            }).ToArray();

            return new Nav(children).Class(cssClass)
                .Attribute("aria-label", texts[titleKey]);
        }

        private bool Matches(ShellLink link) => link.Href == "/"
            ? currentPath == "/"
            : string.Equals(currentPath, link.Href, StringComparison.OrdinalIgnoreCase)
                || currentPath.StartsWith(link.Href + "/", StringComparison.OrdinalIgnoreCase);

        // The most specific page of an area, so /Admin/Acquisition/Profiles is not also the indexers.
        private ShellLink? Current(IReadOnlyList<ShellLink> pages) => pages.Where(Matches).MaxBy(page => page.Href.Length);

        // An area with one page is a plain link.
        private static ShellLink Group(string key, string icon, params ShellLink[] pages) =>
            pages.Length == 1 ? pages[0] with { Icon = icon } : new ShellLink(key, pages[0].Href, icon, pages);

        private sealed class Aside(params IUiComponent[] children) : HtmlElement("aside", children);
    }

    public sealed record ShellLink(string Key, string Href, string Icon, IReadOnlyList<ShellLink>? Children = null);

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
