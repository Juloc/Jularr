namespace Jularr.Frontend.Framework
{
    public static class AdminNavigation
    {
        public static UiSubnav Requests { get; } = new([
            new("admin.requests.title", "/Admin/Requests"),
            new("admin.rules.title", "/Admin/Rules"),
            new("admin.users.title", "/Admin/Requests/Users")
        ]);

        public static UiSubnav Acquisition { get; } = new([
            new("admin.acquisition.title", "/Admin/Acquisition"),
            new("admin.profiles.title", "/Admin/Acquisition/Profiles"),
            new("admin.sources.title", "/Admin/Acquisition/Sources"),
            new("admin.takeover.title", "/Admin/Acquisition/Takeover")
        ]);

        public static UiSubnav Works { get; } = new([
            new("admin.works.title", "/Admin/Works"),
            new("admin.matches.title", "/Admin/Works/Matches")
        ]);

        // Hide unavailable configuration links.
        public static UiSubnav RequestsFor(bool canReadRules) => canReadRules ? Requests : new([new("admin.requests.title", "/Admin/Requests")]);
    }
}
