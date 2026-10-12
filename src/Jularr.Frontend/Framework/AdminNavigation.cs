namespace Jularr.Frontend.Framework
{
    public static class AdminNavigation
    {
        public static UiSubnav Requests { get; } = new([
            new("admin.requests.title", "/Admin/Requests"),
            new("admin.rules.title", "/Admin/Rules"),
            new("admin.users.title", "/Admin/Requests/Users")
        ]);

        // Hide unavailable configuration links.
        public static UiSubnav RequestsFor(bool canReadRules) => canReadRules ? Requests : new([new("admin.requests.title", "/Admin/Requests")]);
    }
}
