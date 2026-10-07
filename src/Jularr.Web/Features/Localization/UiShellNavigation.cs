using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Localization;

/// <summary>
/// One rendered link. <see cref="Groups"/> is set on a section anchor (Admin, Settings) whose
/// child pages are listed beneath it, either expanded in the sidebar or on a drill-in screen.
/// <see cref="IsSection"/> marks Admin and Settings even while they are collapsed. <see cref="AreaKey"/> names the area
/// (Settings, Admin) of a destination listed outside it, so two pages called AI stay distinguishable in the Unfinished list.
/// </summary>
public sealed record UiNavigationItem(
    string Id,
    string LabelKey,
    string Href,
    string Icon,
    bool IsActive,
    IReadOnlyList<UiNavigationGroup>? Groups = null,
    bool IsSection = false,
    string? AreaKey = null)
{
    public bool IsExpanded => Groups is not null;

    /// <summary>
    /// Set on an open section whose current page is listed in the Unfinished section instead of among its own children, so the
    /// section anchor does not claim the current page as well.
    /// </summary>
    public bool CurrentPageListedElsewhere { get; init; }

    /// <summary>True when this link itself is the current page, not only its section.</summary>
    public bool IsCurrentPage =>
        IsActive && !CurrentPageListedElsewhere && !(Groups?.SelectMany(group => group.Items).Any(item => item.IsActive) ?? false);
}

/// <summary>The back link and current page of the content header: the owning area and, inside it, the page that is open.</summary>
public sealed record UiBreadcrumb(UiNavigationItem Parent, UiNavigationItem? Current);

/// <summary>A titled group of child pages inside Admin or Settings.</summary>
public sealed record UiNavigationGroup(string TitleKey, IReadOnlyList<UiNavigationItem> Items);

/// <summary>
/// One destination in <see cref="UiNavigationCatalog"/>. <see cref="Matches"/> are the path roots
/// that mark it active (the href's path when empty); <see cref="Exact"/> matches the href only.
/// <see cref="Sections"/> are the grouped child pages of a section anchor. <see cref="Policy"/> is the
/// <see cref="JularrPolicies"/> policy an account needs to see the entry; null means every account.
/// <see cref="MediaRoutes"/> tie the entry to the media types its routes serve: it is absent for a
/// profile that cannot browse any of them (#598). <see cref="Tabs"/> makes the entry the hub of
/// those tabs: it is shown while at least one tab is, and opens the first visible one. <see cref="Unfinished"/> marks a
/// destination whose core feature is still incomplete or deferred (#870): the shell lists it only in the one subordinate
/// Unfinished section and never beside the finished destinations of its own area.
/// </summary>
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
    bool Unfinished = false);

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
            MediaRoutes: [new("/Books", [WorkMediaType.Book])])
    ];

    /// <summary>Every consumer route that only exists for the media types it serves (#598).</summary>
    public static IEnumerable<UiMediaRoute> MediaRoutes =>
        LibraryTabs.SelectMany(tab => tab.MediaRoutes ?? []);

    public static readonly UiNavigationSection[] Admin =
    [
        new("nav.group.adminPeople",
        [
            new("admin-overview", "admin.nav.overview", "/Admin", "admin", Exact: true, Policy: JularrPolicies.AdminMedia),
            new("admin-users", "admin.nav.users", "/Admin/Users", "users", ["/Admin/Users", "/Admin/User", "/Admin/Roles", "/Admin/Capabilities"], Policy: JularrPolicies.AdminSystem),
            new("admin-requests", "admin.nav.requests", "/Admin/Requests", "requests", Policy: JularrPolicies.AdminMedia, Module: InstanceModule.Acquisition),
            new("admin-wanted", "admin.nav.wanted", "/Admin/Wanted", "download", Policy: JularrPolicies.AdminMedia, Module: InstanceModule.Acquisition)
        ]),
        new("nav.group.adminMedia",
        [
            new("admin-music", "admin.nav.music", "/Admin/Music", "library", Policy: JularrPolicies.AdminMedia, Modules: [InstanceModule.Music, InstanceModule.Acquisition]),
            new("admin-profiles", "admin.nav.profiles", "/Admin/AcquisitionProfiles", "stack", Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition),
            new("admin-usenet", "admin.nav.usenet", "/Admin/Usenet", "download", ["/Admin/Usenet", "/Settings/Indexers", "/Settings/DownloadClients"], Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition),
            new("admin-providers", "admin.nav.providers", "/Admin/Providers", "providers", Policy: JularrPolicies.AdminSystem),
            new("admin-anime-acquisition", "admin.nav.animeAcquisition", "/Acquisition", "library", ["/Acquisition"], Policy: JularrPolicies.AdminMedia, Modules: [InstanceModule.Anime, InstanceModule.Acquisition]),
            new("admin-import", "admin.nav.importSettings", "/Settings/Acquisition", "folder", ["/Settings/Acquisition", "/Settings/Naming", "/Settings/ReadingNaming"], Policy: JularrPolicies.AcquisitionSettings, Module: InstanceModule.Acquisition),
            new("admin-mapping", "admin.nav.mapping", "/Settings/MappingReview", "link", ["/Settings/MappingReview", "/Settings/MappingSegments"], Policy: JularrPolicies.MappingEdit),
            new("admin-subtitles", "admin.nav.subtitles", "/Admin/Subtitles", "subtitles", ["/Admin/Subtitles", "/Settings/Subtitles"], Policy: JularrPolicies.AdminMedia, Unfinished: true),
            new("admin-sonarr", "admin.nav.sonarr", "/Admin/Sonarr", "sync", ["/Admin/Sonarr", "/Settings/Sonarr", "/Settings/SonarrMigration"], Policy: JularrPolicies.AdminSystem, Module: InstanceModule.Acquisition)
        ]),
        new("nav.group.adminSystem",
        [
            new("admin-operations", "admin.nav.operations", "/Admin/Operations", "activity", ["/Admin/Operations", "/Admin/Operation"], Policy: JularrPolicies.AdminMedia),
            new("admin-sessions", "admin.nav.sessions", "/Admin/Sessions", "activity", Policy: JularrPolicies.SessionsStopOthers),
            new("admin-devices", "admin.devices.navLabel", "/Admin/Devices", "devices", Policy: JularrPolicies.AdminSystem),
            new("admin-scans", "admin.nav.scans", "/Admin/Scans", "scan", Policy: JularrPolicies.AdminMedia),
            new("admin-logs", "admin.nav.logs", "/Admin/Logs", "logs", Policy: JularrPolicies.AdminMedia),
            new("admin-ai", "admin.nav.ai", "/Admin/Ai", "spark", Policy: JularrPolicies.AdminSystem, Unfinished: true),
            new("admin-localization", "admin.nav.localization", "/LocalizationAdmin", "globe", Policy: JularrPolicies.AdminSystem),
            new("admin-api-keys", "admin.nav.apiKeys", "/Settings/ApiKeys", "key", Policy: JularrPolicies.AdminSystem),
            new("admin-instance", "admin.nav.instance", "/Admin/Instance", "settings", Policy: JularrPolicies.AdminSystem),
            new("admin-system", "admin.nav.system", "/Admin/System", "server", Policy: JularrPolicies.AdminSystem),
            new("admin-transcoding", "admin.nav.transcoding", "/Admin/Transcoding", "server", Policy: JularrPolicies.AdminSystem),
            new("admin-health", "admin.nav.health", "/Admin/Health", "pulse", Policy: JularrPolicies.AdminSystem)
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
            new("settings-language", "settings.nav.language", "/Settings/Language", "globe", ["/Settings/Language", "/LocalizationPreferences"]),
            new("settings-notifications", "settings.nav.notifications", "/Profile/Notifications", "bell")
        ]),
        new("nav.group.settingsLearning",
        [
            new("settings-learning", "settings.nav.learning", "/Settings/Learning", "learn", ["/Settings/Learning", "/Settings/LearningCourses", "/Settings/LearningScope"], Module: InstanceModule.Learning, Unfinished: true),
            new("settings-ai", "settings.nav.ai", "/Settings/Ai", "spark", Unfinished: true)
        ]),
        new("nav.group.settingsConnections",
        [
            new("settings-anilist", "settings.nav.anilist", "/Settings/AniList", "sync", Module: InstanceModule.Tracking),
            new("settings-offline", "settings.nav.offline", "/Settings/Offline", "download", Unfinished: true)
        ])
    ];

    public static readonly UiNavigationEntry[] App =
    [
        new("home", "nav.home", "/", "home", Exact: true),
        new("watchlist", "nav.watchlist", "/Watchlist", "watchlist", ["/Watchlist", "/Franchises"]),
        new("calendar", "nav.calendar", "/Calendar", "calendar"),
        new("library", "nav.library", "/Library", "library", Tabs: LibraryTabs),
        new("learn", "nav.learn", "/Learn", "learn", ["/Learn", "/Statistics", "/Kana"], RequiresLearning: true, Module: InstanceModule.Learning, Unfinished: true)
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

    /// <summary>The Profile page list, in order, by catalog id. The Unfinished entry closes it when the account has any unfinished destination.</summary>
    public static readonly string[] ProfileLinkIds = ["settings-account", "activity", "profile-devices", "settings", "admin"];

    /// <summary>
    /// The Profile drill-in (<c>/Profile/unfinished</c>) that lists every unfinished destination on a phone. It is a section anchor
    /// of the shell, not a page of its own, so it is not part of <see cref="All"/>.
    /// </summary>
    public static readonly UiNavigationEntry UnfinishedSection = new("unfinished", "nav.unfinished", "/Profile/unfinished", "unfinished");

    /// <summary>
    /// Phone bottom bar, in order (docs/UX.md, INFORMATION_ARCHITECTURE.md, mockups/home): Home, Library, Calendar and Profile.
    /// Learning returns between Calendar and Profile once it is no longer flagged <see cref="UiNavigationEntry.Unfinished"/>.
    /// Everything else is reached from Profile or search; a bottom-bar slot is never given to search or to Unfinished.
    /// </summary>
    public static readonly string[] MobilePrimaryIds = ["home", "library", "calendar", "profile"];

    /// <summary>
    /// Every destination flagged <see cref="UiNavigationEntry.Unfinished"/>, in the order the Unfinished section lists it: consumer
    /// destinations, then personal Settings pages, then Admin pages. The area key names Settings or Admin for the pages that live there.
    /// </summary>
    public static IEnumerable<(UiNavigationEntry Entry, string? AreaKey)> UnfinishedEntries =>
        App.Where(entry => entry.Unfinished).Select(entry => (entry, (string?)null))
            .Concat(UnfinishedOf(Settings, "nav.settings"))
            .Concat(UnfinishedOf(Admin, "nav.admin"));

    private static IEnumerable<(UiNavigationEntry Entry, string? AreaKey)> UnfinishedOf(UiNavigationSection[] sections, string areaKey) =>
        sections.SelectMany(section => section.Entries).Where(entry => entry.Unfinished).Select(entry => (entry, (string?)areaKey));

    /// <summary>Readers whose sidebar shows the open book, novel or manga with its progress.</summary>
    public static readonly string[] CurrentReadingRoots = ["/Books/Read", "/Novels/Read", "/Manga/Read"];

    /// <summary>Every entry that has its own page, including section children.</summary>
    public static IEnumerable<UiNavigationEntry> All =>
        App.Concat(Secondary)
            .Concat(Admin.Concat(Settings).SelectMany(section => section.Entries))
            .Append(ProfileDevices)
            .Append(ProfileActivity);

    /// <summary>Every path root of a set of entries, used to decide which section a page belongs to.</summary>
    public static IEnumerable<string> Roots(IEnumerable<UiNavigationEntry> entries) =>
        entries.SelectMany(RootsOf);

    /// <summary>
    /// The path roots that mark one entry active: its explicit matches, else the roots of its tabs
    /// or media routes, else the href's own path.
    /// </summary>
    public static IEnumerable<string> RootsOf(UiNavigationEntry entry) =>
        entry.Matches
        ?? (entry.Tabs is { } tabs ? Roots(tabs).ToArray() : null)
        ?? entry.MediaRoutes?.Select(route => route.Root).ToArray()
        ?? [PathOf(entry.Href)];

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

/// <summary>
/// Canonical destination model for the shared app shell, built from
/// <see cref="UiNavigationCatalog"/>. The desktop sidebar renders <see cref="Primary"/> and
/// <see cref="Secondary"/>; inside Admin or Settings that item carries its grouped child pages
/// and the rest of the sidebar stays. <see cref="Unfinished"/> is the one subordinate section that closes the sidebar (#870).
/// The phone bottom bar renders <see cref="MobilePrimary"/>; every other destination is on the Profile page
/// (<see cref="BuildProfile"/>, with Unfinished behind it) or behind search. Labels are UI catalog keys.
/// </summary>
public sealed record UiShellNavigation(
    IReadOnlyList<UiNavigationItem> Primary,
    IReadOnlyList<UiNavigationItem> Secondary,
    IReadOnlyList<UiNavigationItem> MobilePrimary,
    IReadOnlyList<UiNavigationItem> Unfinished,
    bool ShowCurrentReading = false)
{
    public const int MaxMobilePrimaryItems = 5;

    /// <summary>Title key of the Unfinished group that closes the Admin and Settings drill-in lists.</summary>
    public const string UnfinishedGroupTitleKey = "nav.group.unfinished";

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
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null)
    {
        var media = visibleMediaTypes ?? WorkMediaTypes.All;
        var modules = enabledInstanceModules ?? AllInstanceModules;

        // Admin pages that live under /Settings belong to Admin, not to Settings.
        var admin = UiNavigationCatalog.Secondary.Single(entry => entry.Id == "admin");
        var inAdmin = Visible(admin, learningVisible, can, media, modules) && IsUnder(path, UiNavigationCatalog.Roots(UiNavigationCatalog.Admin));
        var inSettings = !inAdmin && IsUnder(path, UiNavigationCatalog.Roots(UiNavigationCatalog.Settings));

        var primary = UiNavigationCatalog.App
            .Where(entry => !entry.Unfinished && Visible(entry, learningVisible, can, media, modules))
            .Select(entry => ToItem(entry, IsActive(entry, path), media))
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

        var unfinished = UnfinishedItems(path, learningVisible, can, media, modules);

        // On a phone, every destination outside the bottom bar is reached through Profile.
        var all = primary.Concat(secondary).Concat(unfinished).ToArray();
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
        return new UiShellNavigation(primary, secondary, mobilePrimary, unfinished, showCurrentReading) { Breadcrumb = breadcrumb };
    }

    /// <summary>
    /// The Profile page: <c>Links</c> in catalog order (Admin and Settings open their drill-in
    /// list), and <c>Elsewhere</c>, the remaining destinations the phone bottom bar has no room for.
    /// </summary>
    public static (IReadOnlyList<UiNavigationItem> Links, IReadOnlyList<UiNavigationItem> Elsewhere) BuildProfile(
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null,
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null)
    {
        var media = visibleMediaTypes ?? WorkMediaTypes.All;
        var modules = enabledInstanceModules ?? AllInstanceModules;
        var entries = UiNavigationCatalog.All.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var links = UiNavigationCatalog.ProfileLinkIds
            .Where(id => id != UiNavigationCatalog.ProfileDevices.Id || UiNavigationCatalog.DevicesPageAvailable)
            .Select(id => entries[id])
            .Where(entry => Visible(entry, learningVisible, can, media, modules))
            .Select(entry => entry.Sections is null
                ? ToItem(entry, false, media)
                : ToItem(entry, false, media) with { Href = DrillInHref(entry.Id) })
            .ToList();

        // Unfinished closes the list; a phone has no sidebar section, so this row is its only way in.
        if (UnfinishedItems(PathString.Empty, learningVisible, can, media, modules).Length > 0)
        {
            links.Add(ToItem(UiNavigationCatalog.UnfinishedSection, false) with { Href = DrillInHref(UiNavigationCatalog.UnfinishedSection.Id) });
        }

        var elsewhere = UiNavigationCatalog.App.Concat(UiNavigationCatalog.Secondary)
            .Where(entry => !entry.Unfinished && Visible(entry, learningVisible, can, media, modules))
            .Where(entry => !UiNavigationCatalog.MobilePrimaryIds.Contains(entry.Id)
                && !UiNavigationCatalog.ProfileLinkIds.Contains(entry.Id))
            .Select(entry => ToItem(entry, false, media))
            .ToArray();

        return (links, elsewhere);
    }

    /// <summary>
    /// The drill-in list of Admin or Settings (each ends with its own Unfinished group) or the Unfinished list itself, or null when the
    /// section is not available to the account.
    /// </summary>
    public static UiNavigationItem? BuildSection(
        string? sectionId,
        Func<string, bool> can,
        IReadOnlySet<InstanceModule>? enabledInstanceModules = null,
        bool learningVisible = true,
        IReadOnlyCollection<WorkMediaType>? visibleMediaTypes = null)
    {
        var modules = enabledInstanceModules ?? AllInstanceModules;
        if (string.Equals(sectionId, UiNavigationCatalog.UnfinishedSection.Id, StringComparison.OrdinalIgnoreCase))
        {
            var items = UnfinishedItems(PathString.Empty, learningVisible, can, visibleMediaTypes ?? WorkMediaTypes.All, modules);
            return items.Length == 0
                ? null
                : ToItem(UiNavigationCatalog.UnfinishedSection, false) with { Groups = [new UiNavigationGroup(UnfinishedGroupTitleKey, items)] };
        }

        var entry = UiNavigationCatalog.Secondary.FirstOrDefault(candidate =>
            candidate.Sections is not null
            && string.Equals(candidate.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        return entry is null || !Allowed(entry, learningVisible: true, can, modules)
            ? null
            : Expand(entry, PathString.Empty, can, modules, includeUnfinished: true);
    }

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
        (entry.Policy is null || can(entry.Policy))
        && (!entry.RequiresLearning || learningVisible)
        && (entry.Module is null || enabledInstanceModules.Contains(entry.Module.Value))
        && (entry.Modules is null || entry.Modules.All(enabledInstanceModules.Contains));

    /// <summary>An entry that is not media-scoped is always reachable; otherwise one browsable media type is enough.</summary>
    private static bool ReachesMedia(UiNavigationEntry entry, IReadOnlyCollection<WorkMediaType> media)
    {
        var types = UiNavigationCatalog.MediaTypesOf(entry).ToArray();
        return types.Length == 0 || types.Any(media.Contains);
    }

    /// <summary>
    /// Every unfinished destination the account may reach, in catalog order. The sidebar shows it as the one Unfinished section
    /// and the phone reaches it through Profile; Admin and Settings sidebar sections never list these entries themselves.
    /// </summary>
    private static UiNavigationItem[] UnfinishedItems(
        PathString path,
        bool learningVisible,
        Func<string, bool> can,
        IReadOnlyCollection<WorkMediaType> media,
        IReadOnlySet<InstanceModule> enabledInstanceModules) =>
        UiNavigationCatalog.UnfinishedEntries
            .Where(candidate => Visible(candidate.Entry, learningVisible, can, media, enabledInstanceModules))
            .Select(candidate => ToItem(candidate.Entry, IsActive(candidate.Entry, path), media) with { AreaKey = candidate.AreaKey })
            .ToArray();

    private static UiNavigationItem Expand(
        UiNavigationEntry anchor,
        PathString path,
        Func<string, bool> can,
        IReadOnlySet<InstanceModule> enabledInstanceModules,
        bool includeUnfinished = false)
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
                    .Where(entry => !entry.Unfinished && entries.Contains(entry))
                    .Select(entry => ToItem(entry, entry.Id == active))
                    .ToArray()))
            .Where(group => group.Items.Count > 0)
            .ToList();

        var unfinished = entries.Where(entry => entry.Unfinished).Select(entry => ToItem(entry, entry.Id == active)).ToArray();
        if (includeUnfinished && unfinished.Length > 0)
        {
            groups.Add(new UiNavigationGroup(UnfinishedGroupTitleKey, unfinished));
        }

        return ToItem(anchor, path.HasValue) with
        {
            Groups = groups,
            CurrentPageListedElsewhere = !includeUnfinished && unfinished.Any(item => item.IsActive)
        };
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
            return string.Equals(current, href.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? href.Length : -1;
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
