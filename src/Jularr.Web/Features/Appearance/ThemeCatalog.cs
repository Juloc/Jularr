namespace Jularr.Web.Features.Appearance;

/// <summary>
/// The finite, built-in set of application themes. A theme changes presentation tokens only;
/// routes, navigation, markup and media data stay owned by the application.
/// </summary>
public sealed record AppThemeDefinition(
    string Id,
    string NameKey,
    string DescriptionKey,
    string LogoPath,
    bool UsesOriginalArtwork);

public static class ThemeCatalog
{
    public const string Original = "original";
    public const string CleanPurple = "clean-purple";

    public static IReadOnlyList<AppThemeDefinition> All { get; } =
    [
        new(Original, "theme.catalog.original.name", "theme.catalog.original.description", "/brand/jularr-mark.svg", true),
        new(CleanPurple, "theme.catalog.cleanPurple.name", "theme.catalog.cleanPurple.description", "/brand/jularr-play.svg", false)
    ];

    public static bool TryGet(string? id, out AppThemeDefinition theme)
    {
        theme = All.FirstOrDefault(candidate =>
            candidate.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? All[0];
        return theme.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeOrOriginal(string? id) =>
        TryGet(id, out var theme) ? theme.Id : Original;
}
