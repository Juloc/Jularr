namespace Jularr.Frontend.Framework;

public sealed record ShellLink(string Key, string Href, string Icon);

// Only unconditional routes are linked. Module/profile navigation awaits an
// authorized effective-capabilities read. Never assume Watchlist is enabled.
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

    public IReadOnlyList<ShellLink> Mobile { get; } =
    [
        new("nav.home", "/", "home"),
        new("nav.settings", "/Settings", "settings")
    ];
}
