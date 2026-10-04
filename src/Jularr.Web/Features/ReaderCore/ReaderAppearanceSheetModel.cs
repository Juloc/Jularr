namespace Jularr.Web.Features.ReaderCore;

public sealed record ReaderSheetPaper(string Value, string Label);

/// <summary>
/// Reading-source choices of the sheet's translation group. The values are the ones the page's adapter already binds
/// through data-reader-view for its language menu, so the sheet adds no second view-switching path.
/// </summary>
/// <param name="BothLabel">Null when the renderer cannot show the original and the translation together.</param>
/// <param name="GenerateLabel">Set only while a translation can still be generated for the current user.</param>
public sealed record ReaderSheetTranslation(string OriginalValue, string GeneratedValue, string BothValue, string OriginalLabel, string GeneratedLabel, string? BothLabel, bool GeneratedAvailable, string? GenerateLabel);

/// <param name="IdPrefix">Prefix of the element ids, unique per reader page.</param>
/// <param name="LayoutPreferences">Renders the layout toggles (hyphenation, indent, page numbers, illustrations, auto-continue).</param>
/// <param name="BackgroundArtwork">Renders the themed background choices that the reader's personalization script fills in.</param>
public sealed record ReaderAppearanceSheetModel(string IdPrefix, ReaderCapabilities Capabilities, IReadOnlyList<ReaderSheetPaper> Papers, ReaderSheetTranslation? Translation, bool LayoutPreferences, bool BackgroundArtwork);
