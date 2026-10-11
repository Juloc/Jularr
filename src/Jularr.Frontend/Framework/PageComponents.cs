namespace Jularr.Frontend.Framework;

// Presentation structure only; service-bound values are deliberately not invented.
public sealed record UiHeading(string TitleKey, string? DetailKey = null) : IUiComponent;

public sealed record UiNavItem(string Key, string Href);
public sealed record UiSubnav(IReadOnlyList<UiNavItem> Items) : IUiComponent;

// A scope is descriptive until a real list-query contract can drive selection and counts.
public sealed record UiScope(IReadOnlyList<string> Keys) : IUiComponent;

public sealed record UiToolbar(
    bool Search = true,
    bool Filters = true,
    bool Sort = true,
    bool ViewSwitch = false) : IUiComponent;

public sealed record UiTable(IReadOnlyList<string> Headers) : IUiComponent;

// Empty result sets are not claimed before a service has actually returned them.
public sealed record UiMediaRail(string HeadingKey) : IUiComponent;
public sealed record UiMediaGrid() : IUiComponent;
public sealed record UiDetailHero() : IUiComponent;
public sealed record UiMediaColumns() : IUiComponent;
public sealed record UiPlayer() : IUiComponent;

public sealed record UiSettingRow(string Key, string? DescriptionKey = null);
public sealed record UiSettingsGroup(string HeadingKey, IReadOnlyList<UiSettingRow> Rows) : IUiComponent;

public sealed record UiSection(string HeadingKey, IReadOnlyList<IUiComponent> Items) : IUiComponent;
