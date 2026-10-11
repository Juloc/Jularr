namespace Jularr.Frontend.Framework;

public sealed record ShellLink(string Key, string Href, string Icon);

public sealed class ShellNavigation
{
    public IReadOnlyList<ShellLink> Primary { get; } =
    [
        new("nav.home", "/", "home"),
        new("nav.library", "/Library", "library"),
        new("nav.watchlist", "/Watchlist", "bookmark"),
        new("nav.calendar", "/Calendar", "calendar")
    ];

    public IReadOnlyList<ShellLink> Secondary { get; } =
    [
        new("nav.settings", "/Settings", "settings"),
        new("nav.profile", "/Profile", "user")
    ];

    public IReadOnlyList<ShellLink> Mobile { get; } =
    [
        new("nav.home", "/", "home"),
        new("nav.library", "/Library", "library"),
        new("nav.watchlist", "/Watchlist", "bookmark"),
        new("nav.calendar", "/Calendar", "calendar"),
        new("nav.profile", "/Profile", "user")
    ];
}
