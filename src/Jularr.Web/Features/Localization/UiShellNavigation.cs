using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Localization;

/// <summary>A shared navigation destination, optionally expanded into groups.</summary>
public sealed record UiNavigationItem(
    string Id,
    string LabelKey,
    string Href,
    string Icon,
    bool IsActive,
    IReadOnlyList<UiNavigationGroup>? Groups = null,
    bool IsSection = false)
{
    public bool IsExpanded => Groups is not null;

    /// <summary>True when this link itself is the current page, not only its section.</summary>
    public bool IsCurrentPage =>
        IsActive && !(Groups?.SelectMany(group => group.Items).Any(item => item.IsActive) ?? false);
}

/// <summary>The back link and current page of the content header: the owning area and, inside it, the page that is open.</summary>
public sealed record UiBreadcrumb(UiNavigationItem Parent, UiNavigationItem? Current);

/// <summary>A titled group of child pages inside Admin or Settings.</summary>
public sealed record UiNavigationGroup(string TitleKey, IReadOnlyList<UiNavigationItem> Items);

/// <summary>A canonical destination with permission/module gates and contextual child destinations. Hidden features retain their routes but are not advertised.</summary>
public sealed record UiNavigationEntry(
    string Id,
    string LabelKey,
    string Href,
    string Icon,
    string[]? Matches = null,
    bool Exact = false,
    string? Policy = null,
    bool RequiresLearning = false,
    UiNavigationSection[]? Sections = null,
    UiMediaRoute[]? MediaRoutes = null,
    UiNavigationEntry[]? Tabs = null,
    InstanceModule? Module = null,
    InstanceModule[]? Modules = null,
    bool Hidden = false,
    UiNavigationEntry[]? Links = null);

/// <summary>
/// A consumer route root (the URL prefix of a page folder) and the media types it serves. A
/// profile reaches the route when it can at least browse one of <see cref="MediaTypes"/>. This is
/// the one table behind both the sidebar/tabs and the page-level route gate.
/// </summary>
public sealed record UiMediaRoute(string Root, WorkMediaType[] MediaTypes);

public sealed record UiNavigationSection(string TitleKey, UiNavigationEntry[] Entries);

/// <summary>
/// Every shell destination in one place. To move, rename, regroup or hide a link, edit this
/// table only: sidebar, mobile bar, Profile, Library tabs and tests all read from it. Each page
/// appears once; lists that show a page again (Profile) refer to it by id.
/// </summary>
public static class UiNavigationCatalog
{
    /// <summary>Id of the Library tab that serves every video type; the Library page renders its scopes itself.</summary>
    public const string LibraryVideoTabId = "library-video";

    /// <summary>
    /// Media types inside Library. The Library destination is active on all of them. The video tab is one
    /// destination: Anime, Series and Movies are scopes of the same page, not separate routes. The Anime,
    /// Movie and Series pages under /Library are narrower roots of their own, so a profile that cannot browse the type
    /// does not reach them through the shared Library hub. The Watch player serves Movies and Series; it also checks
    /// the media type of the Work it opens.
    /// </summary>
    public static readonly UiNavigationEntry[] LibraryTabs =
    [
        new(LibraryVideoTabId, "nav.libraryTab.video", "/Library", "library",
            MediaRoutes:
            [
                new("/Library", [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie]),
                new("/Library/Anime", [WorkMediaType.Anime]),
                new("/Library/AnimeRepair", [WorkMediaType.Anime]),
                new("/Library/Episode", [WorkMediaType.Anime]),
                new("/Library/Movie", [WorkMediaType.Movie]),
                new("/Library/Series", [WorkMediaType.Series]),
                new("/Library/Watch", [WorkMediaType.Movie, WorkMediaType.Series]),
                new("/Library/PresentationGroups", [WorkMediaType.Anime]),
                new("/Library/Rename", [WorkMediaType.Anime])
            ]),
        new("library-reading", "nav.libraryTab.reading", "/Reading", "reading",
            MediaRoutes:
            [
                new("/Reading", [WorkMediaType.Manga, WorkMediaType.LightNovel]),
                new("/Novels", [WorkMediaType.LightNovel]),
                new("/Manga", [WorkMediaType.Manga])
            ]),
        new("library-books", "nav.books", "/Books", "books",
            MediaRoutes: [new("/Books", [WorkMediaType.Book])]),
        new("library-music", "nav.libraryTab.music", "/Music", "headphones",
            MediaRoutes: [new("/Music", [WorkMediaType.Music])])
    ];

    /// <summary>Every consumer route that only exists for the media types it serves (#598).</summary>
    public static IEnumerable<UiMediaRoute> MediaRoutes =>
        LibraryTabs.SelectMany(tab => tab.MediaRoutes ?? []);

    public static readonly UiNavigationSection[] Admin =
    [
        new("nav.group.management",
        [
            new("admin-overview", "admin.dashboard.title", "/Admin", "admin", ["/Admin", "/Admin/Search"], Exact: true, Policy: JularrPolicies.AdminMedia, Links:
            [
                new("admin-search", "admin.search.title", "/Admin/Search", "search", Exact: true, Policy: JularrPolicies.AdminMedia)
            ]),
            new("admin-library", "admin.nav.library", "/Admin/Library", "library", ["/Admin/Library", "/Admin/Media", "/Admin/Books", "/Admin/ManualSearch", "/Admin/ReadingManualSearch", "/Admin/BookManualSearch"], Policy: JularrPolicies.AdminMedia, Links:
            [
                new("admin-music", "admin.nav.music", "/Admin/Music", "headphones", Policy: JularrPolicies.AdminMedia, Modules: [InstanceModule.Music, InstanceModule.Acquisition]),
                new("admin-mapping", "admin.nav.mapping", "/Settings/MappingReview", "link", ["/Settings/MappingReview", "/Settings/MappingSegments"], Policy: JularrPolicies.MappingEdit),
                new("admin-merge-review", "admin.nav.mergeReview", "/Admin/MergeReview", "link", Policy: JularrPolicies.AdminMedia),
                new("admin-reconciliation", "admin.nav.reconciliation", "/Admin/LibraryReconciliation", "sync", Policy: JularrPolicies.AdminMedia),
                new("admin-subtitles", "admin.nav.subtitles", "/Admin/Subtitles", "subtitles", ["/Admin/Subtitles", "/Settings/Subtitles"], Policy: JularrPolicies.AdminMedia)
            ]),
            new("admin-requests", "admin.nav.requests", "/Admin/Requests", "requests", Policy: JularrPolicies.AdminMedia, Module: InstanceModule.Acquisition, Links:
            [
                new("admin-request-settings", "admin.requests.section.rules", "/Admin/Requests/Settings", "settings", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition, Links:
                [
                    new("admin-manual-add", "admin.requests.policiesHeading", "/Admin/Capabilities/Manual", "download", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition)
                ]),
                new("admin-request-users", "admin.nav.users", "/Admin/Requests/Users", "users", Policy: JularrPolicies.AdminSystem, Module: InstanceModule.Acquisition)
            ]),
            new("admin-wanted", "admin.nav.missingUpgrades", "/Admin/Wanted", "download", Policy: JularrPolicies.AdminMedia, Module: InstanceModule.Acquisition),
            new("admin-operations", "admin.nav.operations", "/Admin/Operations", "activity", ["/Admin/Operations", "/Admin/Operation", "/Admin/History", "/Admin/Sessions", "/Admin/Scans"], Policy: JularrPolicies.AdminMedia, Links:
            [
                new("admin-history", "admin.nav.history", "/Admin/History", "history", Policy: JularrPolicies.AdminMedia),
                new("admin-sessions", "admin.nav.sessions", "/Admin/Sessions", "activity", Policy: JularrPolicies.SessionsStopOthers),
                new("admin-scans", "admin.nav.scans", "/Admin/Scans", "scan", Policy: JularrPolicies.AdminMedia)
            ]),
            new("admin-usenet", "admin.nav.downloader", "/Admin/Usenet", "download", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition, Links:
            [
                new("admin-clients", "settings.downloadClients.title", "/Settings/DownloadClients", "download", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition),
                new("admin-anime-acquisition", "admin.nav.animeAcquisition", "/Acquisition", "library", Policy: JularrPolicies.AdminMedia, Modules: [InstanceModule.Anime, InstanceModule.Acquisition])
            ])
        ]),
        new("nav.group.configuration",
        [
            new("admin-providers", "admin.nav.providers", "/Admin/Providers", "providers", Policy: JularrPolicies.AdminSystem, Links:
            [
                new("admin-indexers", "settings.indexers.title", "/Settings/Indexers", "search", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition),
                new("admin-sonarr", "admin.nav.sonarr", "/Admin/Sonarr", "sync", ["/Admin/Sonarr", "/Settings/Sonarr", "/Settings/SonarrMigration"], Policy: JularrPolicies.AdminSystem, Module: InstanceModule.Acquisition)
            ]),
            new("admin-profiles", "admin.profiles.title", "/Admin/AcquisitionProfiles", "settings", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition, Links:
            [
                new("admin-import", "admin.nav.importSettings", "/Settings/Acquisition", "folder", ["/Settings/Acquisition", "/Settings/Naming", "/Settings/ReadingNaming"], Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition)
            ]),
            new("admin-storage", "admin.storage.title", "/Admin/Storage", "folder", Policy: JularrPolicies.AdminSystem)
        ]),
        new("nav.group.system",
        [
            new("admin-users", "admin.nav.usersPermissions", "/Admin/Users", "users", ["/Admin/Users", "/Admin/User", "/Admin/Roles", "/Admin/Capabilities"], Policy: JularrPolicies.AdminSystem, Links:
            [
                new("admin-roles", "admin.roles.title", "/Admin/Roles", "users", Policy: JularrPolicies.AdminSystem),
                new("admin-capabilities", "admin.capabilities.title", "/Admin/Capabilities", "settings", Exact: true, Policy: JularrPolicies.AdminSystem)
            ]),
            new("admin-settings", "nav.settings", "/Admin/Instance", "settings", Policy: JularrPolicies.AdminSystem, Links:
            [
                new("admin-appearance", "admin.nav.appearance", "/Admin/Appearance", "palette", Policy: JularrPolicies.AdminSystem),
                new("admin-localization", "admin.nav.localization", "/Admin/Languages", "globe", ["/Admin/Languages", "/LocalizationAdmin"], Policy: JularrPolicies.AdminSystem),
                new("admin-api-keys", "admin.nav.apiKeys", "/Settings/ApiKeys", "key", Policy: JularrPolicies.AdminSystem)
            ]),
            new("admin-diagnostics", "admin.nav.diagnostics", "/Admin/Resources", "server", Policy: JularrPolicies.AdminMedia, Links:
            [
                new("admin-database", "admin.database.title", "/Admin/Database", "server", Policy: JularrPolicies.AdminSystem),
                new("admin-system", "admin.nav.system", "/Admin/System", "server", Policy: JularrPolicies.AdminSystem),
                new("admin-transcoding", "admin.nav.transcoding", "/Admin/Transcoding", "server", Policy: JularrPolicies.AdminSystem),
                new("admin-health", "admin.nav.health", "/Admin/Health", "pulse", Policy: JularrPolicies.AdminSystem),
                new("admin-logs", "admin.nav.logs", "/Admin/Logs", "logs", Policy: JularrPolicies.AdminMedia),
                new("admin-devices", "admin.devices.navLabel", "/Admin/Devices", "devices", Policy: JularrPolicies.AdminSystem)
            ])
        ])
    ];

    /// <summary>Personal pages only; server configuration belongs to <see cref="Admin"/>.</summary>
    public static readonly UiNavigationSection[] Settings =
    [
        new("nav.group.settingsYou",
        [
            new("settings-overview", "settings.nav.overview", "/Settings", "settings", Exact: true),
            new("settings-account", "settings.nav.account", "/Profile/Account", "profile"),
            new("settings-appearance", "settings.nav.appearance", "/Settings/Appearance", "palette", ["/Settings/Appearance", "/Appearance"]),
            new("settings-home", "settings.nav.home", "/Settings/Home", "home"),
            new("settings-language", "settings.nav.language", "/Settings/Language", "globe", ["/Settings/Language", "/LocalizationPreferences"]),
            new("settings-notifications", "settings.nav.notifications", "/Profile/Notifications", "bell")
        ]),
        new("nav.group.settingsLearning",
        [
            new("settings-learning", "settings.nav.learning", "/Settings/Learning", "learn", ["/Settings/Learning", "/Settings/LearningCourses", "/Settings/LearningScope"], Module: InstanceModule.Learning, Hidden: true),
            new("settings-ai", "settings.nav.ai", "/Settings/Ai", "spark", Hidden: true)
        ]),
        new("nav.group.settingsConnections",
        [
            new("settings-anilist", "settings.nav.anilist", "/Settings/AniList", "sync", Module: InstanceModule.Tracking),
            new("settings-offline", "settings.nav.offline", "/Settings/Offline", "download")
        ])
    ];

    public static readonly UiNavigationEntry[] App =
    [
        new("home", "nav.home", "/", "home", Exact: true),
        new("library", "nav.library", "/Library", "library", Tabs: LibraryTabs),
        new("watchlist", "nav.watchlist", "/Watchlist", "watchlist", ["/Watchlist", "/Franchises"]),
        new("calendar", "nav.calendar", "/Calendar", "calendar"),
        new("learn", "nav.learn", "/Learn", "learn", ["/Learn", "/Statistics", "/Kana"], RequiresLearning: true, Module: InstanceModule.Learning, Hidden: true)
    ];

    public static readonly UiNavigationEntry[] Secondary =
    [
        new("admin", "nav.admin", "/Admin", "admin", Policy: JularrPolicies.AdminMedia, Sections: Admin),
        new("settings", "nav.settings", "/Settings", "settings", Sections: Settings),
        new("profile", "nav.profile", "/Profile", "profile", ["/Profile", "/Activity"])
    ];

    /// <summary>
    /// The Devices page belongs to #518. Set this once <c>Pages/Profile/Devices.cshtml</c>
    /// exists; a test keeps the two in sync.
    /// </summary>
    public static readonly bool DevicesPageAvailable = true;

    public static readonly UiNavigationEntry ProfileDevices =
        new("profile-devices", "nav.devices", "/Profile/Devices", "devices");

    /// <summary>Activity is a Profile tab (docs/mockups/profile-activity): it has no sidebar entry and is listed in the account menu.</summary>
    public static readonly UiNavigationEntry ProfileActivity = new("activity", "nav.activity", "/Activity", "history");

    /// <summary>The account menu destinations in catalog order.</summary>
    public static readonly string[] ProfileLinkIds = ["settings-account", "activity", "profile-devices", "settings", "admin"];

    /// <summary>Phone destinations; Arr mode omits the consumer Library.</summary>
    public static readonly string[] MobilePrimaryIds = ["home", "library", "watchlist", "calendar", "profile"];

    /// <summary>Readers whose sidebar shows the open book, novel or manga with its progress.</summary>
    public static readonly string[] CurrentReadingRoots = ["/Books/Read", "/Novels/Read", "/Manga/Read"];

    /// <summary>Every entry that has its own page, including section children.</summary>
    public static IEnumerable<UiNavigationEntry> All =>
        App.Concat(Secondary)
            .Concat(Admin.Concat(Settings).SelectMany(section => section.Entries).SelectMany(Descendants))
            .Append(ProfileDevices)
            .Append(ProfileActivity);

    private static IEnumerable<UiNavigationEntry> Descendants(UiNavigationEntry entry) => new[] { entry }.Concat((entry.Links ?? []).SelectMany(Descendants));

    /// <summary>Every path root of a set of entries, used to decide which section a page belongs to.</summary>
    public static IEnumerable<string> Roots(IEnumerable<UiNavigationEntry> entries) =>
        entries.SelectMany(RootsOf);

    /// <summary>
    /// The path roots that mark one entry active: its explicit matches, else the roots of its tabs
    /// or media routes, else the href's own path.
    /// </summary>
    public static IEnumerable<string> RootsOf(UiNavigationEntry entry) =>
        (entry.Matches ?? (entry.Tabs is { } tabs ? Roots(tabs).ToArray() : null) ?? entry.MediaRoutes?.Select(route => route.Root).ToArray() ?? [PathOf(entry.Href)])
        .Concat((entry.Links ?? []).SelectMany(RootsOf));

    /// <summary>The media types an entry (or its tabs) serves; empty for an entry that is not media-scoped.</summary>
    public static IEnumerable<WorkMediaType> MediaTypesOf(UiNavigationEntry entry) =>
        (entry.Tabs is { } tabs
            ? tabs.SelectMany(MediaTypesOf)
            : entry.MediaRoutes?.SelectMany(route => route.MediaTypes) ?? [])
        .Distinct();

    public static IEnumerable<string> Roots(UiNavigationSection[] sections) =>
        Roots(sections.SelectMany(section => section.Entries));

    public static string PathOf(string href)
    {
        var cut = href.IndexOfAny(['?', '#']);
        return cut < 0 ? href : href[..cut];
    }
}

/// <summary>Shared sidebar, mobile navigation and contextual breadcrumbs, derived only from the canonical catalog.</summary>
public sealed record UiShellNavigation(
    IReadOnlyList<UiNavigationItem> Primary,
    IReadOnlyList<UiNavigationItem> Secondary,
    IReadOnlyList<UiNavigationItem> MobilePrimary,
    bool ShowCurrentReading = false)
{
    public const int MaxMobilePrimaryItems = 5;

    /// <summary>Where the page sits inside Admin, Settings or Profile, for the back link in the content header; null on a top-level page.</summary>
    public UiBreadcrumb? Breadcrumb { get; init; }

    /// <summary>The section expanded in the sidebar, if any. Never more than one.</summary>
    public UiNavigationItem? Expanded => Secondary.FirstOrDefault(item => item.IsExpanded);

    /// <param name="visibleMediaTypes">
    /// The media types the profile may browse (<c>IAppShellService</c>); a media type outside it
    /// leaves no destination behind (#598). Null means the caller does not scope by media type.
    /// </param>
    public static UiShellNavigation Build(
        PathString path,
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null,
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null,
        bool mediaManagerMode = false)
    {
        var media = visibleMediaTypes ?? WorkMediaTypes.All;
        var modules = enabledInstanceModules ?? AllInstanceModules;

        // Admin pages that live under /Settings belong to Admin, not to Settings.
        var admin = UiNavigationCatalog.Secondary.Single(entry => entry.Id == "admin");
        var inAdmin = Visible(admin, learningVisible, can, media, modules) && IsUnder(path, UiNavigationCatalog.Roots(UiNavigationCatalog.Admin));
        var inSettings = !inAdmin && IsUnder(path, UiNavigationCatalog.Roots(UiNavigationCatalog.Settings));

        var primary = UiNavigationCatalog.App
            .Where(entry => !entry.Hidden && Visible(entry, learningVisible, can, media, modules))
            .Where(entry => !mediaManagerMode || entry.Id != "library")
            .Select(entry => ToItem(
                mediaManagerMode && entry.Id == "home" ? entry with { LabelKey = "nav.discover", Icon = "search", Href = "/Discover" } : entry,
                mediaManagerMode && entry.Id == "home" ? path.StartsWithSegments("/Discover") : IsActive(entry, path),
                media))
            .ToArray();
        var secondary = UiNavigationCatalog.Secondary
            .Where(entry => Visible(entry, learningVisible, can, media, modules))
            .Select(entry => entry.Id switch
            {
                "admin" => inAdmin ? Expand(entry, path, can, modules) : ToItem(entry, false, media),
                "settings" => inSettings ? Expand(entry, path, can, modules) : ToItem(entry, false, media),
                _ => ToItem(entry, !inAdmin && !inSettings && IsActive(entry, path), media)
            })
            .ToArray();


        // On a phone, every destination outside the bottom bar is reached through Profile.
        var all = primary.Concat(secondary).ToArray();
        var elsewhereActive = all.Any(item => item.IsActive && !UiNavigationCatalog.MobilePrimaryIds.Contains(item.Id));
        var mobilePrimary = UiNavigationCatalog.MobilePrimaryIds
            .Select(id => all.FirstOrDefault(item => item.Id == id))
            .OfType<UiNavigationItem>()
            .Select(item => item.Id == "profile" ? item with { IsActive = item.IsActive || elsewhereActive } : item)
            .Take(MaxMobilePrimaryItems)
            .ToArray();

        var showCurrentReading = !inAdmin && !inSettings && IsUnder(path, UiNavigationCatalog.CurrentReadingRoots);
        var profile = secondary.FirstOrDefault(item => item.Id == "profile");
        var expanded = secondary.FirstOrDefault(item => item.IsExpanded);
        var breadcrumb = expanded is not null
            ? new UiBreadcrumb(expanded with { Groups = null }, expanded.Groups!.SelectMany(group => group.Items).FirstOrDefault(item => item.IsActive))
            : profile is not null && IsUnder(path, [UiNavigationCatalog.PathOf(UiNavigationCatalog.ProfileActivity.Href)])
                ? new UiBreadcrumb(profile, ToItem(UiNavigationCatalog.ProfileActivity, true, media))
                : profile is not null && path.StartsWithSegments("/Profile") && path.Value?.TrimEnd('/').Length > "/Profile".Length
                    ? new UiBreadcrumb(profile, null)
                    : null;
        var contextParent = ContextualParent(path, can, modules);
        var contextChild = contextParent?.Links?.Where(entry => Allowed(entry, true, can, modules) && IsActive(entry, path)).OrderByDescending(entry => MatchLength(entry, path)).FirstOrDefault();
        if (contextParent is not null && contextChild is null)
        {
            var ancestor = UiNavigationCatalog.All.FirstOrDefault(entry => Allowed(entry, true, can, modules) && (entry.Links?.Any(child => child.Id == contextParent.Id) ?? false));
            if (ancestor is not null)
            {
                contextChild = contextParent;
                contextParent = ancestor;
            }
        }

        if (contextParent is not null && contextChild is not null)
        {
            breadcrumb = new UiBreadcrumb(ToItem(contextParent, false), ToItem(contextChild, true));
        }

        return new UiShellNavigation(primary, secondary, mobilePrimary, showCurrentReading) { Breadcrumb = breadcrumb };
    }

    /// <summary>
    /// The Profile page: <c>Links</c> in catalog order (Admin and Settings open their drill-in
    /// list), and <c>Elsewhere</c>, the remaining destinations the phone bottom bar has no room for.
    /// </summary>
    public static (IReadOnlyList<UiNavigationItem> Links, IReadOnlyList<UiNavigationItem> Elsewhere) BuildProfile(
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null,
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null,
        bool mediaManagerMode = false)
    {
        var media = visibleMediaTypes ?? WorkMediaTypes.All;
        var modules = enabledInstanceModules ?? AllInstanceModules;
        var entries = UiNavigationCatalog.All.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var links = UiNavigationCatalog.ProfileLinkIds
            .Where(id => id != UiNavigationCatalog.ProfileDevices.Id || UiNavigationCatalog.DevicesPageAvailable)
            .Select(id => entries[id])
            .Where(entry => Visible(entry, learningVisible, can, media, modules))
            .Where(entry => !mediaManagerMode || entry.Id != "library")
            .Select(entry => entry.Sections is null
                ? ToItem(entry, false, media)
                : ToItem(entry, false, media) with { Href = DrillInHref(entry.Id) })
            .ToList();

        var elsewhere = UiNavigationCatalog.App.Concat(UiNavigationCatalog.Secondary)
            .Where(entry => !entry.Hidden && Visible(entry, learningVisible, can, media, modules))
            .Where(entry => !mediaManagerMode || entry.Id != "library")
            .Where(entry => !UiNavigationCatalog.MobilePrimaryIds.Contains(entry.Id)
                && !UiNavigationCatalog.ProfileLinkIds.Contains(entry.Id))
            .Select(entry => ToItem(entry, false, media))
            .ToArray();

        return (links, elsewhere);
    }

    /// <summary>The permission-scoped Admin or Settings drill-in list.</summary>
    public static UiNavigationItem? BuildSection(
        string? sectionId,
        Func<string, bool> can,
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null,
        bool learningVisible = true,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null)
    {
        var modules = enabledInstanceModules ?? AllInstanceModules;
        var entry = UiNavigationCatalog.Secondary.FirstOrDefault(candidate =>
            candidate.Sections is not null
            && string.Equals(candidate.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        return entry is null || !Allowed(entry, learningVisible: true, can, modules)
            ? null
            : Expand(entry, PathString.Empty, can, modules);
    }


    public static IReadOnlyList<UiNavigationItem> BuildContextualLinks(PathString path, Func<string, bool> can, IReadOnlySet<InstanceModule> modules)
    {
        var parent = ContextualParent(path, can, modules);
        return parent is null ? [] : new[] { parent }.Concat(parent.Links ?? [])
            .Where(entry => Allowed(entry, true, can, modules))
            .Select(entry => ToItem(entry, string.Equals(UiNavigationCatalog.PathOf(entry.Href), path.Value?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>Searchable Admin destinations, including contextual children, with the same gates as normal navigation.</summary>
    public static IReadOnlyList<UiNavigationGroup> BuildAdminSearchDestinations(Func<string, bool> can, IReadOnlySet<InstanceModule> modules)
    {
        if (!can(JularrPolicies.AdminMedia))
        {
            return [];
        }

        IEnumerable<(UiNavigationEntry Entry, bool IsSettings)> Descendants(IEnumerable<UiNavigationEntry> entries, bool isSettings)
        {
            foreach (var entry in entries.Where(entry => Allowed(entry, true, can, modules)))
            {
                var configuration = isSettings || entry.Id is "admin-settings" or "admin-request-settings" or "admin-capabilities";
                yield return (entry, configuration);
                foreach (var child in Descendants(entry.Links ?? [], configuration))
                {
                    yield return child;
                }
            }
        }

        var destinations = UiNavigationCatalog.Admin.SelectMany(section => Descendants(section.Entries, section.TitleKey == "nav.group.configuration")).ToArray();
        return new[] { false, true }.Select(isSettings => new UiNavigationGroup(isSettings ? "admin.search.settings" : "admin.search.pages",
            destinations.Where(destination => destination.IsSettings == isSettings).Select(destination => ToItem(destination.Entry, false)).ToArray())).ToArray();
    }

    private static UiNavigationEntry? ContextualParent(PathString path, Func<string, bool> can, IReadOnlySet<InstanceModule> modules) =>
        UiNavigationCatalog.All.Where(entry => entry.Links is { Length: > 0 } && Allowed(entry, true, can, modules) && IsActive(entry, path))
            .OrderByDescending(entry => UiNavigationCatalog.PathOf(entry.Href).Length).FirstOrDefault();

    public static string DrillInHref(string sectionId) => $"/Profile/{sectionId}";

    /// <summary>
    /// Library media-type tabs, only the media types the profile may browse; exactly one is active
    /// on any Library page. A profile with a single visible type has nothing to switch between.
    /// </summary>
    public static IReadOnlyList<UiNavigationItem> BuildLibraryTabs(
        PathString path,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null)
    {
        var media = visibleMediaTypes ?? WorkMediaTypes.All;
        return UiNavigationCatalog.LibraryTabs
            .Where(entry => ReachesMedia(entry, media))
            .Select(entry => ToItem(entry, IsActive(entry, path), media))
            .ToArray();
    }

    private static bool Visible(
        UiNavigationEntry entry,
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlyCollection<WorkMediaType> media,
        IReadOnlySet<InstanceModule> enabledInstanceModules) =>
        Allowed(entry, learningVisible, can, enabledInstanceModules) && ReachesMedia(entry, media);

    /// <summary>Account policy, profile Learning state and server-wide module availability.</summary>
    private static bool Allowed(
        UiNavigationEntry entry,
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlySet<InstanceModule> enabledInstanceModules) =>
        !entry.Hidden && (entry.Policy is null || can(entry.Policy))
        && (!entry.RequiresLearning || learningVisible)
        && (entry.Module is null || enabledInstanceModules.Contains(entry.Module.Value))
        && (entry.Id != "settings-offline" || enabledInstanceModules.Overlaps([InstanceModule.Playback, InstanceModule.Book, InstanceModule.Novel, InstanceModule.Manga]))
        && (entry.Modules is null || entry.Modules.All(enabledInstanceModules.Contains));

    /// <summary>An entry that is not media-scoped is always reachable; otherwise one browsable media type is enough.</summary>
    private static bool ReachesMedia(UiNavigationEntry entry, IReadOnlyCollection<WorkMediaType> media)
    {
        var types = UiNavigationCatalog.MediaTypesOf(entry).ToArray();
        return types.Length == 0 || types.Any(media.Contains);
    }

    private static UiNavigationItem Expand(
        UiNavigationEntry anchor,
        PathString path,
        Func<string, bool> can,
        IReadOnlySet<InstanceModule> enabledInstanceModules)
    {
        // Admin and Settings pages are account-level, never scoped to a media type.
        var entries = anchor.Sections!
            .SelectMany(section => section.Entries)
            .Where(entry => Allowed(entry, learningVisible: true, can, enabledInstanceModules))
            .ToArray();

        // The most specific match wins, so "/Admin" (overview) is not active on "/Admin/Users".
        var active = entries
            .Select(entry => (entry, length: MatchLength(entry, path)))
            .Where(candidate => candidate.length >= 0)
            .OrderByDescending(candidate => candidate.length)
            .Select(candidate => candidate.entry.Id)
            .FirstOrDefault();

        var groups = anchor.Sections!
            .Select(section => new UiNavigationGroup(
                section.TitleKey,
                section.Entries
                    .Where(entry => !entry.Hidden && entries.Contains(entry))
                    .Select(entry => ToItem(entry, entry.Id == active))
                    .ToArray()))
            .Where(group => group.Items.Count > 0)
            .ToList();

        return ToItem(anchor, path.HasValue) with { Groups = groups };
    }

    private static UiNavigationItem ToItem(UiNavigationEntry entry, bool isActive) =>
        new(entry.Id, entry.LabelKey, entry.Href, entry.Icon, isActive, IsSection: entry.Sections is not null);

    /// <summary>A hub of tabs (Library) opens the first tab the profile may browse.</summary>
    private static UiNavigationItem ToItem(UiNavigationEntry entry, bool isActive, IReadOnlyCollection<WorkMediaType> media) =>
        ToItem(entry, isActive) with
        {
            Href = entry.Tabs?.FirstOrDefault(tab => ReachesMedia(tab, media))?.Href ?? entry.Href
        };

    private static bool IsActive(UiNavigationEntry entry, PathString path) => MatchLength(entry, path) >= 0;

    /// <summary>Length of the matching root, or -1 when the entry does not match the path.</summary>
    private static int MatchLength(UiNavigationEntry entry, PathString path)
    {
        if (!path.HasValue)
        {
            return -1;
        }

        var href = UiNavigationCatalog.PathOf(entry.Href);
        if (entry.Exact)
        {
            var current = path.Value!.TrimEnd('/');
            return (entry.Matches ?? [href]).Where(root => string.Equals(current, root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)).Select(root => root.Length).DefaultIfEmpty(-1).Max();
        }

        return UiNavigationCatalog.RootsOf(entry)
            .Where(root => path.StartsWithSegments(root, StringComparison.OrdinalIgnoreCase))
            .Select(root => root.Length)
            .DefaultIfEmpty(-1)
            .Max();
    }

    private static bool IsUnder(PathString path, IEnumerable<string> roots) =>
        roots.Any(root => path.StartsWithSegments(root, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<InstanceModule> AllInstanceModules { get; } =
        Enum.GetValues<InstanceModule>().ToHashSet();
}
