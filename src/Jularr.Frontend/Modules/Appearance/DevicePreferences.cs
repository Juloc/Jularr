using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Modules.Appearance
{
    // The device's choices its browser also keeps as cookies (DevicePreference.ts), so a page arrives already in them and nothing changes after its
    // first paint. Only the known values count; anything else is the default.
    public static class DevicePreferences
    {
        public static string Skin(HttpRequest request) => request.Cookies["jularr-ui-skin"] is "original" ? "original" : "clean";

        public static string Mode(HttpRequest request) => request.Cookies["jularr-ui-mode"] is "light" or "dark" ? request.Cookies["jularr-ui-mode"]! : "system";

        // Personal lists as rows instead of the grid.
        public static string? ListLayout(HttpRequest request) => request.Cookies["list-layout"] is "list" ? "list" : null;

        // Discover's shelves wrapped into a grid.
        public static string? DiscoverLayout(HttpRequest request) => request.Cookies["discover-layout"] is "grid" ? "grid" : null;
    }
}
