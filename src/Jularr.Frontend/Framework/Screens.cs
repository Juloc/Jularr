namespace Jularr.Frontend.Framework;

public static class Screens
{
    public static bool Populate(string? route, Page page)
    {
        var key = route?.TrimEnd('/').ToLowerInvariant();
        return key switch
        {
            "" or "/" or "discover" => UserScreens.Discover(page),
            "details" => UserScreens.Details(page),
            "player" => UserScreens.Player(page),
            "watchlist" => UserScreens.Watchlist(page),
            "settings" => UserScreens.Settings(page),
            "admin-requests" => AdminScreens.Requests(page),
            "admin-rules" => AdminScreens.Rules(page),
            "admin-users" => AdminScreens.Users(page),
            "admin-wanted" => AdminScreens.Wanted(page),
            "admin-works" => AdminScreens.Works(page),
            "admin-work" => AdminScreens.Work(page),
            "admin-library" => AdminScreens.Library(page),
            _ => false
        };
    }

    public static string? RouteFromPath(string? path)
    {
        var value = path?.TrimEnd('/').ToLowerInvariant() ?? "";
        if (value.StartsWith("/details/", StringComparison.Ordinal)) return "details";
        if (value.StartsWith("/player/", StringComparison.Ordinal)) return "player";
        if (value.StartsWith("/admin/works/", StringComparison.Ordinal)) return "admin-work";
        return value switch
        {
            "" or "/" or "/discover" => "discover",
            "/watchlist" => "watchlist",
            "/settings" => "settings",
            "/admin/requests" => "admin-requests",
            "/admin/rules" => "admin-rules",
            "/admin/requests/users" => "admin-users",
            "/admin/wanted" => "admin-wanted",
            "/admin/works" => "admin-works",
            "/admin/library" => "admin-library",
            _ => null
        };
    }
}
