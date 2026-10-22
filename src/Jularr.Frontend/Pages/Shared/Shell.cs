using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Pages.Shared
{
    // Shared application shell: header with the page's breadcrumb, and a sidebar in user or admin mode (icon-only on a tablet,
    // off-canvas on a phone, as in the mockups).
    public sealed class Shell(string pageTitleKey, string currentPath, ShellNavigation navigation, FrontendAssets assets, string? searchQuery = null, string? searchType = null,
        IReadOnlyList<Crumb>? parents = null, string? currentLabel = null, AdminCounts? counts = null, IQueryCollection? query = null) : IUiComponent
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

            // Administration is its own sidebar mode: every group open, the user entries out of the way; the user menu has one link per area, taken from the same menu.
            var adminPath = currentPath.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);
            var menu = AdminMenu.For(texts);
            var current = AdminMenu.Current(menu, currentPath, key => query?[key].ToString());
            secondary.AddRange(AdminMenu.UserLinks(texts).Select(area => new ShellLink(area.Entry.LabelKey, area.Href, area.Entry.Icon)));
            var adminMode = menu.Count > 0 && adminPath;

            // The menu button of the phone, then the trail, the one global search, the theme switch, the inbox and the avatar.
            var header = new List<IUiComponent>
            {
                new Button(new Icon("menu", assets)).IconOnly(texts["nav.menu"]).Attribute("class", "button button--icon app-menu-toggle")
                    .Attribute("data-sidebar-toggle", "true").Attribute("aria-controls", "app-sidebar").Attribute("aria-expanded", "false"),
                new Brand(assets, compact: true),
                Breadcrumb(menu, current, texts)
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
                new Aside(new Brand(assets), new Div(new Text("nav.administration")).Class("nav-eyebrow"), AdminNavigation(menu, current, texts))
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

        // The administration as the sidebar shows it: a find field, then every group open under a thin line with its small name, each entry with its own icon
        // and, for three of them, the figure of what waits.
        private IUiComponent AdminNavigation(IReadOnlyList<AdminMenuGroupView> menu, (AdminMenuEntryView Entry, AdminMenuPage Page)? current, UiText texts)
        {
            var find = new Div(new SearchBox("/Admin", "admin.menu.find", null, null, null, new Icon("search", assets, 16), scripted: true)).Class("menu-find").Attribute("data-menu-find", "");
            var groups = menu.Select(group =>
            {
                var entries = group.Entries.Select(entry => Entry(entry, entry == current?.Entry, texts));
                return (IUiComponent)new Section(new H3(new Span(new Text(group.Group.LabelKey))).Class("menu-group__label"), new Div([.. entries]).Class("menu-group__entries"))
                    .Class(group.Entries.Any(entry => entry == current?.Entry) ? "menu-group menu-group--current" : "menu-group")
                    .Attribute("aria-label", texts[group.Group.LabelKey]).Attribute("data-menu-text", texts[group.Group.LabelKey]);
            });
            return new Nav([find, .. groups]).Class("menu").Attribute("aria-label", texts["nav.admin"]);
        }

        private IUiComponent Entry(AdminMenuEntryView view, bool active, UiText texts)
        {
            var label = texts[view.Entry.LabelKey];
            var children = new List<IUiComponent> { new Span(new Icon(view.Entry.Icon, assets, 16)).Class("menu-entry__icon"), new Span(new Truncated(label)).Class("menu-entry__label") };

            // The figure of the entry, kept current by the live stream; it shows only while it is not zero.
            if (view.Entry.Counter != AdminCounter.None && counts?.Of(view.Entry.Counter) is { } count)
            {
                var badge = new Span(new ValueText(count.ToString(CultureInfo.CurrentCulture))).Class("menu-badge")
                    .Attribute("data-live", LiveKeys.Menu).Attribute("data-live-text", AdminCounts.Name(view.Entry.Counter));
                children.Add(count > 0 ? badge : badge.Attribute("hidden", ""));
            }

            // What the find field looks for: the entry and the pages behind it.
            var text = string.Join(" ", view.Pages.Select(page => texts[page.LabelKey]).Prepend(label));
            var anchor = new A([.. children]).Class(active ? "menu-entry is-active" : "menu-entry").Attribute("href", view.Href).Attribute("data-menu-text", text);
            return active ? anchor.Attribute("aria-current", "page") : anchor;
        }

        // Area > Entry > Page > Title: the entry comes from the administration menu, the parents from the page; an entry that is named like the page is left out.
        // The last part names the page (or its item) and is no link; narrow screens show only the last two (CSS).
        private IUiComponent Breadcrumb(IReadOnlyList<AdminMenuGroupView> menu, (AdminMenuEntryView Entry, AdminMenuPage Page)? current, UiText texts)
        {
            var trail = new List<IUiComponent>();
            // A part with sub-pages is also a menu of them; a phone shows the menu instead of the link (CSS).
            void Add(IUiComponent label, string? href, IReadOnlyList<LinkMenuItem>? items = null)
            {
                if (href is null)
                {
                    trail.Add(new Li(label).Attribute("aria-current", "page").Class("breadcrumb__item"));
                    return;
                }

                var link = new A(label).Attribute("href", href);
                trail.Add(items is { Count: > 1 }
                    ? new Li(link.Class("breadcrumb__link"), new LinkMenu(label, items)).Class("breadcrumb__item breadcrumb__item--menu")
                    : new Li(link).Class("breadcrumb__item"));
            }

            var title = parents is { Count: > 0 } && currentLabel is not null ? currentLabel : texts[pageTitleKey];
            var adminTrail = menu.Count > 0 && currentPath.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);
            var entries = menu.SelectMany(group => group.Entries).ToList();
            if (adminTrail)
            {
                Add(new Text("nav.admin"), entries[0].Href, [.. entries.Select(entry => new LinkMenuItem(new Text(entry.Entry.LabelKey), entry.Href, entry == current?.Entry))]);
                if (current is { } place)
                {
                    var names = (parents ?? []).Select(parent => parent.IsKey ? texts[parent.Text] : parent.Text).Append(title);
                    if (!names.Contains(texts[place.Entry.Entry.LabelKey], StringComparer.CurrentCultureIgnoreCase))
                    {
                        Add(new Text(place.Entry.Entry.LabelKey), place.Entry.Href, PageItems(place.Entry, place.Page));
                    }
                }
            }

            foreach (var parent in parents ?? [])
            {
                // A parent that is a page of an entry offers that entry's pages.
                var entry = adminTrail ? entries.FirstOrDefault(candidate => candidate.Pages.Any(page => page.Href() == parent.Href)) : null;
                Add(parent.IsKey ? new Text(parent.Text) : new ValueText(parent.Text), parent.Href, entry is null ? null : PageItems(entry, null));
            }

            Add(parents is { Count: > 0 } && currentLabel is not null ? new ValueText(currentLabel) : new Text(pageTitleKey), null);
            return new Nav(new Ol([.. trail]).Class("breadcrumb")).Class("app-header-location").Attribute("aria-label", texts["nav.breadcrumb"]);
        }

        private static IReadOnlyList<LinkMenuItem> PageItems(AdminMenuEntryView entry, AdminMenuPage? current) =>
            [.. entry.Pages.Select(page => new LinkMenuItem(new Text(page.LabelKey), page.Href(), page == current))];

        private IUiComponent Links(IReadOnlyList<ShellLink> links, string titleKey, string cssClass, UiText texts)
        {
            var children = links.Select(link =>
            {
                var active = Matches(link);
                var anchor = new A(new Icon(link.Icon, assets), new Span(new Text(link.Key)))
                    .Class(active ? "nav-link is-active" : "nav-link")
                    .Attribute("href", link.Href);
                return (IUiComponent)(active ? anchor.Attribute("aria-current", "page") : anchor);
            }).ToArray();

            return new Nav(children).Class(cssClass)
                .Attribute("aria-label", texts[titleKey]);
        }

        private bool Matches(ShellLink link) => link.Href == "/"
            ? currentPath == "/"
            : string.Equals(currentPath, link.Href, StringComparison.OrdinalIgnoreCase)
                || currentPath.StartsWith(link.Href + "/", StringComparison.OrdinalIgnoreCase);

        private sealed class Aside(params IUiComponent[] children) : HtmlElement("aside", children);

        private sealed class Ol(params IUiComponent[] children) : HtmlElement("ol", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);

        private sealed class Kbd(params IUiComponent[] children) : HtmlElement("kbd", children);
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

    }
}
