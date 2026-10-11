namespace Jularr.Frontend.Framework;

// Presentation-only definitions. They never claim that backend data was loaded.
public sealed record UiHeading(string TitleKey, string? DetailKey = null) : IUiComponent;
public sealed record UiHint(string Key) : IUiComponent;
public sealed record UiEmpty(string Key = "skeleton.unconnected") : IUiComponent;
public sealed record UiHero(string HeadingKey, string DescriptionKey) : IUiComponent;
public sealed record UiScope(IReadOnlyList<string> Keys) : IUiComponent;
public sealed record UiTable(IReadOnlyList<string> Headers, string EmptyKey = "skeleton.unconnected") : IUiComponent;
public sealed record UiNavItem(string Key, string Href);
public sealed record UiSubnav(IReadOnlyList<UiNavItem> Items) : IUiComponent;
public sealed record UiGroup(string NameKey, IReadOnlyList<string> ItemKeys) : IUiComponent;
public sealed record UiPlayer() : IUiComponent;
public sealed record UiMediaColumns() : IUiComponent;
