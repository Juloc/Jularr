using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared application shell: header with the page's breadcrumb, and a sidebar in user or admin mode (icon-only on a tablet,
    // off-canvas on a phone, as in the mockups).
    public sealed class Shell(string pageTitleKey, string currentPath, ShellNavigation navigation, FrontendAssets assets, string? searchQuery = null, string? searchType = null,
        IReadOnlyList<Crumb>? parents = null, string? currentLabel = null) : IUiComponent
    {
        private const string AdminPrefix = "/Admin";

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Add available destinations.
            var primary = navigation.Primary.ToList();
            var secondary = navigation.Secondary.ToList();

            if (texts.CanReadMedia())
            {
                var library = new ShellLink("nav.library", "/Library", "library");
                primary.Add(library);
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
            }

            if (texts.CanRequest())
            {
                primary.Add(new ShellLink("requests.title", "/Requests", "home"));
            }

            // Administration is its own sidebar mode: every area open, the user entries out of the way.
            // The Instance area belongs to admin mode only; the user menu keeps the other areas as entry points.
            var adminPath = currentPath.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);
            var administration = AdministrationLinks(texts, adminPath);
            secondary.AddRange(administration);
            var adminMode = administration.Count > 0 && adminPath;

            // The menu button of the phone, then the trail, the one global search, the theme switch, the inbox and the avatar.
            var header = new List<IUiComponent>
            {
                new Button(new Icon("menu", assets)).IconOnly(texts["nav.menu"]).Attribute("class", "button button--icon app-menu-toggle")
                    .Attribute("data-sidebar-toggle", "true").Attribute("aria-controls", "app-sidebar").Attribute("aria-expanded", "false"),
                new Brand(assets, compact: true),
                Breadcrumb(administration, texts)
            };
            if (texts.CanReadMedia())
            {
                var search = new SearchBox("/", "search.global", searchQuery, searchType is null ? null : new Dictionary<string, string> { ["type"] = searchType });
                header.Add(new Div(new Label(new Icon("search", assets)).Class("app-search__icon").Attribute("for", SearchBox.InputId("search.global")).Attribute("aria-hidden", "true"), search,
                    new Kbd(new ValueText("/")).Class("app-search__hint").Attribute("aria-hidden", "true")).Class("app-search"));
            }

            // Inbox with its unread count.
            if (texts.CanNotify())
            {
                var unread = texts.UnreadNotifications;
                header.Add(new A([new Icon("bell", assets), .. unread > 0 ? new IUiComponent[] { new Span(new ValueText(unread > 99 ? "99+" : unread.ToString(CultureInfo.CurrentCulture))).Class("app-notifications__count") } : []])
                    .Class("app-notifications" + (currentPath.StartsWith("/Notifications", StringComparison.OrdinalIgnoreCase) ? " is-active" : ""))
                    .Attribute("href", "/Notifications")
                    .Attribute("aria-label", unread > 0 ? string.Format(CultureInfo.CurrentCulture, texts["notifications.unread"], unread) : texts["notifications.title"]));
            }

            // Light or dark with one click (Modules/Appearance/theme.entry.ts); the icon shows what a click switches to.
            header.Add(new Button(new Span(new Icon("theme-dark", assets)).Class("theme-toggle__moon"), new Span(new Icon("theme-light", assets)).Class("theme-toggle__sun"))
                .IconOnly(texts["nav.theme"]).Attribute("class", "button button--icon theme-toggle").Attribute("data-toggle-mode", "true"));
            if (texts.IsAuthenticated)
            {
                header.Add(new A(texts.ProfileName is { Length: > 0 } name ? new Span(new ValueText(Initials(name))) : new Icon("user", assets, 16))
                    .Class("app-avatar").Attribute("href", "/Settings").Attribute("aria-label", texts["nav.profile"]));
            }

            new Header([.. header]).Class("app-header").WriteTo(writer, encoder, texts);
            // The phone's menu is a drawer over a dimmed page; a tap on the dimmed part closes it.
            new Div().Class("app-sidebar-overlay").Attribute("data-sidebar-overlay", "true").WriteTo(writer, encoder, texts);

            if (adminMode)
            {
                new Aside(new Brand(assets), new Div(new Text("nav.administration")).Class("nav-eyebrow"), Links(administration, "nav.admin", "sidebar-navigation", texts, expand: true))
                    .Class("app-sidebar app-sidebar--admin").Attribute("id", "app-sidebar").WriteTo(writer, encoder, texts);
                return;
            }

            new Aside(
                new Brand(assets),
                Links(primary, "nav.primary", "sidebar-navigation", texts),
                Links(secondary, "nav.secondary", "sidebar-navigation sidebar-secondary", texts)
            ).Class("app-sidebar").Attribute("id", "app-sidebar").WriteTo(writer, encoder, texts);
        }

        // The avatar shows the first letters of the first two names.
        private static string Initials(string name) => string.Concat(name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Select(part => new System.Globalization.StringInfo(part).SubstringByTextElements(0, 1))).ToUpperInvariant();

        // Administration areas open into their pages; an area with one page is a plain link.
        private static List<ShellLink> AdministrationLinks(UiText texts, bool withInstance)
        {
            var links = new List<ShellLink>();
            if (texts.CanAdminister())
            {
                links.Add(Group("nav.group.system", "server", new("nav.system.overview", "/Admin/System", ""), new("admin.jobs.title", "/Admin/Jobs", ""), new("admin.transcoding.title", "/Admin/Transcoding", "")));
            }

            if (texts.CanManageMedia())
            {
                links.Add(Group("nav.group.media", "books", [
                    new("admin.works.title", "/Admin/Works", ""), new("admin.matches.title", "/Admin/Works/Matches", ""), new("admin.activity.title", "/Admin/Activity", ""),
                    new("admin.manualImport.title", "/Admin/ManualImport", ""),
                    .. texts.CanImportMedia() ? new ShellLink[] { new("admin.import.title", "/Admin/Import", "") } : [],
                    new("admin.history.title", "/Admin/History", "")]));
                links.Add(Group("nav.group.library", "library", [new("library.tab.Overview", "/Admin/Library", ""), new("library.tab.Unmatched", "/Admin/Library/Unmatched", ""),
                    new("library.tab.Files", "/Admin/Library/Files", ""),
                    .. texts.CanManageStorage() ? new ShellLink[] { new("library.tab.Naming", "/Admin/Library/Naming", ""), new("library.tab.Rename", "/Admin/Library/Rename", ""),
                        new("library.tab.Versions", "/Admin/Library/Versions", "") } : []]));
            }

            if (texts.CanManageRequests())
            {
                links.Add(Group("nav.group.requests", "requests", [new("admin.requests.title", "/Admin/Requests", ""),
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

            if (acquisition.Count > 0) links.Add(Group("nav.group.acquisition", "download", [.. acquisition]));

            // The instance's own pages, each by its permission (otherwise only the Settings page lists them).
            var instance = new List<ShellLink>();
            if (texts.CanAdminister()) instance.Add(new("admin.instance.title", "/Admin/Instance", ""));
            if (texts.Can("storage.read")) instance.Add(new("storage.title", "/Admin/Storage", ""));
            if (texts.Can("providers.read")) instance.Add(new("admin.providers.title", "/Admin/Providers", ""));
            if (texts.Can("accounts.read")) instance.Add(new("admin.accounts.title", "/Admin/Accounts", ""));
            if (texts.Can("localization.read")) instance.Add(new("admin.localization.title", "/Admin/Localization", ""));
            if (texts.CanAdminister()) instance.Add(new("admin.catalog.title", "/Admin/Catalog", ""));
            if (texts.Can("backup.read")) instance.Add(new("admin.backup.title", "/Admin/Backup", ""));
            if (withInstance && instance.Count > 0) links.Add(Group("nav.group.instance", "settings", [.. instance]));

            return links;
        }

        // Area > Group > Page > Title: the area and group come from the sidebar's own structure, the parents from the page.
        // The last part names the page (or its item) and is no link; narrow screens show only the last two (CSS).
        private IUiComponent Breadcrumb(IReadOnlyList<ShellLink> administration, UiText texts)
        {
            var trail = new List<IUiComponent>();
            // A part with sub-pages is also a menu of them; a phone shows the menu instead of the link (CSS).
            void Add(IUiComponent label, string? href, IReadOnlyList<ShellLink>? menu = null)
            {
                if (href is null)
                {
                    trail.Add(new Li(label).Attribute("aria-current", "page").Class("breadcrumb__item"));
                    return;
                }

                var link = new A(label).Attribute("href", href);
                trail.Add(menu is { Count: > 1 }
                    ? new Li(link.Class("breadcrumb__link"), new LinkMenu(label, [.. menu.Select(entry => new LinkMenuItem(new Text(entry.Key), entry.Href, IsActive(entry)))])).Class("breadcrumb__item breadcrumb__item--menu")
                    : new Li(link).Class("breadcrumb__item"));
            }

            var adminTrail = administration.Count > 0 && currentPath.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);
            if (adminTrail)
            {
                Add(new Text("nav.admin"), administration[0].Href, administration);
                var group = administration.FirstOrDefault(link => link.Children is not null && Current(link.Children) is not null);
                if (group is not null) Add(new Text(group.Key), group.Href, group.Children);
            }

            foreach (var parent in parents ?? [])
            {
                // A parent that is a page of an area offers that area's pages.
                var area = adminTrail ? administration.FirstOrDefault(link => link.Children?.Any(page => page.Href == parent.Href) == true)?.Children : null;
                Add(parent.IsKey ? new Text(parent.Text) : new ValueText(parent.Text), parent.Href, area);
            }

            Add(parents is { Count: > 0 } && currentLabel is not null ? new ValueText(currentLabel) : new Text(pageTitleKey), null);
            return new Nav(new Ol([.. trail]).Class("breadcrumb")).Class("app-header-location").Attribute("aria-label", texts["nav.breadcrumb"]);
        }

        private IUiComponent Links(IReadOnlyList<ShellLink> links, string titleKey, string cssClass, UiText texts, bool expand = false)
        {
            var children = links.Select(link =>
            {
                const string className = "nav-link";
                if (link.Children is { } pages)
                {
                    // An area is a link to its first page, so no click lands on an empty group; the administration lists the pages below it and marks the current one.
                    var current = Current(pages);
                    var heading = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key))).Class(current is null ? className : className + " is-active").Attribute("href", pages[0].Href);
                    if (!expand) return new Div(heading).Class("nav-group");
                    return new Div(heading, new Div([.. pages.Select(page =>
                    {
                        var anchor = new A(new Span(new Text(page.Key))).Class(page == current ? "nav-link nav-link--sub is-active" : "nav-link nav-link--sub").Attribute("href", page.Href);
                        return page == current ? anchor.Attribute("aria-current", "page") : anchor;
                    })]).Class("nav-group__pages")).Class("nav-group");
                }

                var active = IsActive(link);
                var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                    .Class(active ? className + " is-active" : className)
                    .Attribute("href", link.Href);
                if (active) anchor.Attribute("aria-current", "page");
                return (IUiComponent)anchor;
            }).ToArray();

            return new Nav(children).Class(cssClass)
                .Attribute("aria-label", texts[titleKey]);
        }

        // An area is current when one of its pages is.
        private bool IsActive(ShellLink link) => link.Children is { } area ? Current(area) is not null : Matches(link);

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

        private sealed class Ol(params IUiComponent[] children) : HtmlElement("ol", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);

        private sealed class Kbd(params IUiComponent[] children) : HtmlElement("kbd", children);
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

    }
}
