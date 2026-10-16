namespace Jularr.Web.Ui;

public enum UiTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
    Accent
}

/// <summary>Presentation-only tag. Labels are Razor-escaped; tone and optional CSS classes are chosen by the calling renderer, never by a service binding.</summary>
public sealed record UiStatusTagModel(string Label, UiTone Tone = UiTone.Neutral, string? Icon = null, string? CssClass = null);
/// <summary>A compact row cell with a full-value tooltip and optional secondary text; it performs no data lookup.</summary>
public sealed record UiTextCellModel(string Text, string? Secondary = null);
/// <summary>An already-authorized destination. This renderer does not determine permissions, routing or request status.</summary>
public sealed record UiTabModel(string Label, string Href, bool IsActive, string? Icon = null, int? Count = null);
public sealed record UiTabsModel(string Label, IReadOnlyList<UiTabModel> Items, string? CssClass = null);
