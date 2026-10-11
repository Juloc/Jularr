namespace Jularr.Frontend.Framework;

public sealed record ShellLink(string Key, string Href, string Icon);

// Only implemented destinations are linked; unavailable modules stay hidden.
public sealed class ShellNavigation
{
    public IReadOnlyList<ShellLink> Primary { get; } =
    [
        new("nav.home", "/", "home"),
        new("nav.watchlist", "/Watchlist", "bookmark")
    ];

    public IReadOnlyList<ShellLink> Secondary { get; } =
    [
        new("nav.settings", "/Settings", "settings")
    ];

    public IReadOnlyList<ShellLink> Mobile { get; } =
    [
        new("nav.home", "/", "home"),
        new("nav.watchlist", "/Watchlist", "bookmark"),
        new("nav.settings", "/Settings", "settings")
    ];
}
