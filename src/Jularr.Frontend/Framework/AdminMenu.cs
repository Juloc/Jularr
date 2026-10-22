using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // The figure an entry of the administration menu wears: how many downloads are open, how many jobs failed, how many requests wait.
    public enum AdminCounter
    {
        None,
        OpenDownloads,
        FailedJobs,
        OpenRequests
    }

    // One page of an entry, reached by a chip. A pane is a page that shares its address with others and names itself in the query (`?tab=` of the quality rules, which keep the
    // chosen media type; `?view=` of the media servers); the default pane is the one shown without a name.
    public sealed record AdminMenuPage(string LabelKey, string Path, string? Right = null, string[]? Modules = null, string? Pane = null, bool IsDefaultPane = false, string PaneKey = "tab")
    {
        public string Href(string? mediaTypeId = null) => Pane is null ? Path : Path + "?" + PaneKey + "=" + Pane + (string.IsNullOrEmpty(mediaTypeId) ? "" : "&mediaTypeId=" + Uri.EscapeDataString(mediaTypeId));

        // The page is shown for the account: its right inside its enabled modules.
        public bool IsAllowed(UiText texts) => texts.Can(Right, Modules ?? []);
    }

    // One entry of the sidebar with its sibling pages. The entry is shown while one of its pages is allowed, and links to the first of those.
    // An entry with UserMenu is also an area of the user menu, which links to it by one link of its own.
    public sealed record AdminMenuEntry(string Id, string LabelKey, string Icon, IReadOnlyList<AdminMenuPage> Pages, AdminCounter Counter = AdminCounter.None, bool UserMenu = false);

    public sealed record AdminMenuGroup(string LabelKey, IReadOnlyList<AdminMenuEntry> Entries);

    // An entry as one account sees it: only the pages that are allowed.
    public sealed record AdminMenuEntryView(AdminMenuEntry Entry, IReadOnlyList<AdminMenuPage> Pages)
    {
        public string Href => Pages[0].Href();
    }

    public sealed record AdminMenuGroupView(AdminMenuGroup Group, IReadOnlyList<AdminMenuEntryView> Entries);

    // The one definition of the administration: the sidebar's groups and entries, the chip row of every page of an entry and the breadcrumb all follow it.
    public static class AdminMenu
    {
        // A permission is named through Right, so that it is no UI text to the resource check.
        private static string Right(string permission) => permission;

        private static readonly string Admin = Right("instance.settings.read");
        private static readonly string Media = Right("media.manual.update");
        private static readonly string Acquisition = Right("acquisition.manage");

        public static readonly IReadOnlyList<AdminMenuGroup> Groups =
        [
            new("admin.menu.group.operations",
            [
                new("dashboard", "admin.dashboard.title", "dashboard", [new("admin.dashboard.title", "/Admin/Dashboard", Admin)], UserMenu: true),
                new("activity", "admin.menu.activity", "activity",
                [
                    new("admin.activity.title", "/Admin/Activity", Media, ["Library"]),
                    new("admin.wanted.title", "/Admin/Wanted", Right("wanted.update"), ["Requests"]),
                    new("admin.history.title", "/Admin/History", Media, ["Library"])
                ], AdminCounter.OpenDownloads),
                new("jobs", "admin.jobs.title", "pulse",
                [
                    new("admin.jobs.title", "/Admin/Jobs", Admin),
                    new("admin.transcoding.title", "/Admin/Transcoding", Admin, ["Library", "Playback"])
                ], AdminCounter.FailedJobs)
            ]),
            new("admin.menu.group.content",
            [
                new("media", "nav.group.media", "books",
                [
                    new("admin.works.title", "/Admin/Works", Media, ["Library"]),
                    new("admin.matches.title", "/Admin/Works/Matches", Media, ["Library"]),
                    new("admin.manualImport.title", "/Admin/ManualImport", Media, ["Library"]),
                    new("admin.import.title", "/Admin/Import", Media, ["Library", "Requests"])
                ], UserMenu: true),
                new("library", "nav.group.library", "library",
                [
                    new("library.tab.Overview", "/Admin/Library", Media, ["Library"]),
                    new("library.tab.Unmatched", "/Admin/Library/Unmatched", Media, ["Library"]),
                    new("library.tab.Files", "/Admin/Library/Files", Media, ["Library"]),
                    new("library.tab.Rename", "/Admin/Library/Rename", Right("storage.update"), ["Library"]),
                    new("library.tab.Versions", "/Admin/Library/Versions", Right("storage.update"), ["Library"])
                ], UserMenu: true)
            ]),
            new("nav.group.acquisition",
            [
                new("downloads", "admin.menu.downloads", "download",
                [
                    new("acquisition.role.Search", "/Admin/Acquisition", Acquisition, ["Acquisition"]),
                    new("acquisition.role.Download", "/Admin/Acquisition/Downloads", Acquisition, ["Acquisition"]),
                    new("acquisition.role.Subtitles", "/Admin/Acquisition/Subtitles", Acquisition, ["Acquisition", "Subtitles"]),
                    new("admin.profiles.title", "/Admin/Acquisition/Profiles", Acquisition, ["Acquisition"], "profiles", true),
                    new("arr.formats", "/Admin/Acquisition/Profiles", Acquisition, ["Acquisition"], "formats"),
                    new("arr.sizes", "/Admin/Acquisition/Profiles", Acquisition, ["Acquisition"], "sizes"),
                    new("arr.releaseProfiles", "/Admin/Acquisition/Profiles", Acquisition, ["Acquisition"], "release"),
                    new("arr.tags", "/Admin/Acquisition/Profiles", Acquisition, ["Acquisition"], "tags")
                ], UserMenu: true),
                new("sources", "admin.sources.title", "swap",
                [
                    new("admin.sources.title", "/Admin/Acquisition/Sources", Acquisition, ["Acquisition"]),
                    new("admin.takeover.title", "/Admin/Acquisition/Takeover", Acquisition, ["Acquisition"]),
                    new("admin.browser.title", "/Admin/Browser", Acquisition, ["Acquisition"])
                ])
            ]),
            new("admin.menu.group.connections",
            [
                new("integrations", "admin.menu.integrations", "providers",
                [
                    new("admin.providers.title", "/Admin/Providers", Right("providers.read")),
                    new("admin.mediaServers.title", "/Admin/Providers", Right("providers.read"), ["ProgressSync", "Library"], "servers", false, "view"),
                    new("admin.catalog.title", "/Admin/Catalog", Admin, ["Library", "DiscoverCache"])
                ]),
                new("storage", "storage.title", "drive",
                [
                    new("storage.connections", "/Admin/Storage", Right("storage.read")),
                    new("downloadStorage.title", "/Admin/DownloadStorage", Acquisition, ["Acquisition"])
                ])
            ]),
            new("nav.group.instance",
            [
                new("users", "admin.menu.users", "users",
                [
                    new("admin.accounts.title", "/Admin/Accounts", Right("accounts.read")),
                    new("admin.requests.title", "/Admin/Requests", Right("requests.update"), ["Requests"]),
                    new("admin.rules.title", "/Admin/Rules", Right("rules.read"), ["Requests"]),
                    new("admin.users.title", "/Admin/Requests/Users", Right("rules.read"), ["Requests"])
                ], AdminCounter.OpenRequests, true),
                new("settings", "admin.menu.settings", "settings",
                [
                    new("settings.tab.instance", "/Admin/Instance", Admin),
                    new("settings.tab.modules", "/Admin/Instance/Modules", Admin),
                    new("settings.tab.mediaTypes", "/Admin/Instance/MediaTypes", Admin),
                    new("settings.tab.languages", "/Admin/Instance/Languages", Right("localization.read")),
                    new("translation.local.title", "/Admin/Instance/Translation", Admin, ["Translation"]),
                    new("settings.tab.naming", "/Admin/Instance/Naming", Right("storage.read"), ["Library"])
                ]),
                new("localization", "admin.localization.title", "globe", [new("admin.localization.title", "/Admin/Localization", Right("localization.read"))])
            ]),
            new("admin.menu.group.maintenance",
            [
                new("system", "nav.group.system", "server",
                [
                    new("nav.system.server", "/Admin/System", Admin),
                    new("admin.performance.title", "/Admin/Performance", Admin, ["Diagnostics"]),
                    new("admin.logs.title", "/Admin/Logs", Admin)
                ], UserMenu: true),
                new("backups", "admin.backup.title", "shield", [new("admin.backup.title", "/Admin/Backup", Right("backup.read"))])
            ])
        ];

        // What the account may see: the groups with an entry, the entries with a page.
        public static IReadOnlyList<AdminMenuGroupView> For(UiText texts) =>
        [
            .. Groups.Select(group => new AdminMenuGroupView(group, [.. group.Entries
                .Select(entry => new AdminMenuEntryView(entry, [.. entry.Pages.Where(page => page.IsAllowed(texts))]))
                .Where(entry => entry.Pages.Count > 0)])).Where(group => group.Entries.Count > 0)
        ];

        // The areas of the user menu, one link each to the first page the account may open of its entry; an entry that is not visible has no link.
        public static IReadOnlyList<AdminMenuEntryView> UserLinks(UiText texts) =>
            [.. For(texts).SelectMany(group => group.Entries).Where(entry => entry.Entry.UserMenu)];

        // The entry and page an address belongs to: the most specific page, so /Admin/Works/Matches is not also the works, and a pane by what the query names.
        public static (AdminMenuEntryView Entry, AdminMenuPage Page)? Current(IEnumerable<AdminMenuGroupView> menu, string path, Func<string, string?> query)
        {
            (AdminMenuEntryView Entry, AdminMenuPage Page)? best = null;
            var bestScore = -1;
            foreach (var entry in menu.SelectMany(group => group.Entries))
            {
                foreach (var page in entry.Pages)
                {
                    var matches = string.Equals(path, page.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(page.Path + "/", StringComparison.OrdinalIgnoreCase);
                    if (matches && page.Pane is not null)
                    {
                        var named = query(page.PaneKey);
                        matches = string.IsNullOrEmpty(named) ? page.IsDefaultPane : string.Equals(named, page.Pane, StringComparison.OrdinalIgnoreCase);
                    }

                    // A pane that the query names beats the page that shares its address.
                    var score = page.Path.Length * 2 + (page.Pane is null ? 0 : 1);
                    if (matches && score > bestScore)
                    {
                        best = (entry, page);
                        bestScore = score;
                    }
                }
            }

            return best;
        }

        // The one row of the page's entry: its sibling pages as chips and, after a thin divider, the filter chips of the page's own list. A page without siblings and filters gets no row.
        // A page that holds the pane and the scope in its own state (the quality rules after a post) names them; one with filters gives the row the name of its filters.
        // A page that knows how much it holds of a sibling gives the counts by label: a sibling with none is left out (nothing is set up for it), the others show their number.
        public static NavigationTabs Row(UiText texts, HttpRequest request, IReadOnlyList<NavigationTab>? filters = null, string? tab = null, string? mediaTypeId = null, string ariaLabelKey = "admin.menu.pages",
            IReadOnlyDictionary<string, int>? counts = null)
        {
            var tabs = new List<NavigationTab>();
            if (Current(For(texts), request.Path.Value ?? "/", Query(request, tab)) is { Entry.Pages.Count: > 1 } current)
            {
                mediaTypeId ??= request.Query["mediaTypeId"].ToString();
                foreach (var page in current.Entry.Pages)
                {
                    int? count = counts is not null && counts.TryGetValue(page.LabelKey, out var known) ? known : null;
                    if (count == 0 && page != current.Page) continue;
                    tabs.Add(new NavigationTab(page.LabelKey, page.Href(mediaTypeId), page == current.Page, Count: count));
                }
            }

            if (filters is { Count: > 0 })
            {
                var divided = tabs.Count > 0;
                tabs.AddRange(filters.Select((filter, index) => index == 0 && divided ? filter with { Divider = true } : filter));
            }

            return new NavigationTabs(tabs, ariaLabelKey, "filters");
        }

        // The row of a page that is only a sibling: nothing for an entry with one page.
        public static NavigationTabs? Siblings(UiText texts, HttpRequest request) =>
            Current(For(texts), request.Path.Value ?? "/", Query(request, null)) is { Entry.Pages.Count: > 1 } ? Row(texts, request) : null;

        // What the address names; a page's own pane wins over the query.
        public static Func<string, string?> Query(HttpRequest request, string? tab) => key => key == "tab" && tab is not null ? tab : request.Query[key].ToString();
    }
}
