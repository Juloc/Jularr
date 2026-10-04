using Jularr.Web.Features.Speech;

namespace Jularr.Web.Features.ReaderCore;

public enum ReaderContentType
{
    Book,
    LightNovel,
    WebNovel,
    Manga,
    FixedDocument
}

public enum ReaderLayoutKind
{
    ReflowableText,
    ImageSequence,
    FixedPages
}

public sealed record ReaderCapabilities(
    bool SupportsContinuous,
    bool SupportsPaged,
    bool SupportsAutoScroll,
    bool SupportsTypography,
    bool SupportsTextSelection,
    bool SupportsHighlights,
    bool SupportsBookmarks,
    bool SupportsDualLanguage,
    bool SupportsTranslation,
    bool SupportsEmbeddedImages,
    bool SupportsTwoPage,
    bool SupportsPageCurl,
    bool SupportsThemeArtwork,
    bool SupportsZoom,
    bool SupportsVerticalReading,
    bool SupportsTts)
{
    public static ReaderCapabilities For(
        ReaderContentType contentType,
        ReaderLayoutKind layoutKind) =>
        layoutKind switch
        {
            ReaderLayoutKind.ReflowableText => new(
                SupportsContinuous: true,
                SupportsPaged: true,
                SupportsAutoScroll: true,
                SupportsTypography: true,
                SupportsTextSelection: true,
                SupportsHighlights: true,
                SupportsBookmarks: true,
                SupportsDualLanguage: contentType is ReaderContentType.LightNovel
                    or ReaderContentType.WebNovel
                    or ReaderContentType.Book,
                SupportsTranslation: contentType is ReaderContentType.LightNovel
                    or ReaderContentType.WebNovel
                    or ReaderContentType.Book,
                SupportsEmbeddedImages: true,
                SupportsTwoPage: true,
                SupportsPageCurl: true,
                SupportsThemeArtwork: true,
                SupportsZoom: false,
                SupportsVerticalReading: false,
                SupportsTts: true),
            ReaderLayoutKind.ImageSequence => new(
                SupportsContinuous: true,
                SupportsPaged: true,
                SupportsAutoScroll: false,
                SupportsTypography: false,
                SupportsTextSelection: false,
                SupportsHighlights: false,
                SupportsBookmarks: true,
                SupportsDualLanguage: false,
                SupportsTranslation: false,
                SupportsEmbeddedImages: true,
                SupportsTwoPage: true,
                SupportsPageCurl: true,
                SupportsThemeArtwork: false,
                SupportsZoom: true,
                SupportsVerticalReading: true,
                SupportsTts: false),
            _ => new(
                SupportsContinuous: true,
                SupportsPaged: true,
                SupportsAutoScroll: false,
                SupportsTypography: false,
                SupportsTextSelection: false,
                SupportsHighlights: true,
                SupportsBookmarks: true,
                SupportsDualLanguage: false,
                SupportsTranslation: false,
                SupportsEmbeddedImages: true,
                SupportsTwoPage: true,
                SupportsPageCurl: false,
                SupportsThemeArtwork: false,
                SupportsZoom: true,
                SupportsVerticalReading: true,
                // A book in fixed pages (a PDF book) reads its text layer aloud.
                SupportsTts: contentType is ReaderContentType.Book)
        };
}

public sealed record ReaderDocumentDescriptor(
    Guid WorkId,
    ReaderContentType ContentType,
    ReaderLayoutKind LayoutKind,
    string Title,
    IReadOnlyList<string> Genres,
    ReaderCapabilities Capabilities,
    IReadOnlyList<ReaderDocumentLanguage> Languages)
{
    public string ContentTypeKey => ReaderContentTypes.ToKey(ContentType);

    public static ReaderDocumentDescriptor Create(
        Guid workId,
        ReaderContentType contentType,
        string title,
        IReadOnlyList<string>? genres = null,
        ReaderLayoutKind? layoutKind = null,
        IReadOnlyList<ReaderDocumentLanguage>? languages = null)
    {
        var layout = layoutKind ?? ReaderContentTypes.DefaultLayout(contentType);
        return new(
            workId,
            contentType,
            layout,
            title,
            genres ?? [],
            ReaderCapabilities.For(contentType, layout),
            ReaderDocumentLanguage.Distinct(languages));
    }
}

/// <summary>
/// One text language the document can show (source or translation). Language-bound
/// Reader features, such as per-language read-aloud voices, use this list instead of
/// guessing from the page.
/// </summary>
public sealed record ReaderDocumentLanguage(string Tag, string Label)
{
    public static IReadOnlyList<ReaderDocumentLanguage> Distinct(
        IReadOnlyList<ReaderDocumentLanguage>? languages)
    {
        if (languages is null || languages.Count == 0)
        {
            return [];
        }

        var result = new List<ReaderDocumentLanguage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            var tag = SpeechPreferenceResolver.NormalizeLanguageTag(language.Tag);
            if (tag == "und" || !seen.Add(tag))
            {
                continue;
            }

            result.Add(new ReaderDocumentLanguage(
                tag,
                string.IsNullOrWhiteSpace(language.Label) ? tag : language.Label.Trim()));
        }

        return result;
    }
}

public static class ReaderContentTypes
{
    public static string ToKey(ReaderContentType value) =>
        value switch
        {
            ReaderContentType.Book => "book",
            ReaderContentType.LightNovel => "light-novel",
            ReaderContentType.WebNovel => "web-novel",
            ReaderContentType.Manga => "manga",
            ReaderContentType.FixedDocument => "fixed",
            _ => "book"
        };

    public static ReaderContentType Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "book" => ReaderContentType.Book,
            "light-novel" or "light_novel" or "light novel" or "novel" =>
                ReaderContentType.LightNovel,
            "web-novel" or "web_novel" or "web novel" =>
                ReaderContentType.WebNovel,
            "manga" or "comic" => ReaderContentType.Manga,
            "fixed" or "fixed-document" or "pdf" => ReaderContentType.FixedDocument,
            _ => ReaderContentType.Book
        };

    public static ReaderContentType FromNovelMetadata(
        string? format,
        string? sourceProvider)
    {
        var normalized = format?.Trim().Replace('_', ' ').ToLowerInvariant();
        if (normalized is "novel" or "light novel" or "lightnovel")
        {
            return ReaderContentType.LightNovel;
        }

        if (!string.IsNullOrWhiteSpace(sourceProvider))
        {
            return ReaderContentType.WebNovel;
        }

        return ReaderContentType.LightNovel;
    }

    public static ReaderLayoutKind DefaultLayout(ReaderContentType type) =>
        type switch
        {
            ReaderContentType.Manga => ReaderLayoutKind.ImageSequence,
            ReaderContentType.FixedDocument => ReaderLayoutKind.FixedPages,
            _ => ReaderLayoutKind.ReflowableText
        };
}

public sealed record ReaderSystemPreset(
    string ReadingMode,
    string PageTransition,
    bool TwoPageSpread,
    double AutoScrollSpeed,
    string FontFamily,
    double FontSizeRem,
    double LineHeight,
    double ParagraphSpacingEm,
    int TextWidthPx,
    string TextAlignment,
    string ChapterStyle,
    string PaperStyle,
    bool GenreArtworkEnabled,
    string GenreTheme,
    string BackgroundAssetId,
    double BackgroundIntensity,
    string BackgroundMotionMode,
    double ThemeEffectStrength,
    double ThemeBrightness,
    double ThemeContrast,
    double ThemeSaturation,
    double ThemeBlurPx,
    double ThemeVignetteStrength,
    double ThemeGrainStrength,
    double ThemeTextBackdropStrength,
    double ThemeParallaxStrength,
    double ThemeTintStrength,
    string BookmarkStyle,
    string BookmarkColor,
    string TtsProviderId,
    double TtsRate,
    double TtsPitch,
    double TtsVolume,
    bool TtsAutoContinueChapters);

public static class ReaderPresetCatalog
{
    public static ReaderSystemPreset For(ReaderContentType contentType) =>
        contentType switch
        {
            ReaderContentType.LightNovel => Base() with
            {
                ReadingMode = "paged",
                ChapterStyle = "light-novel",
                GenreArtworkEnabled = true,
                BackgroundMotionMode = "auto",
                BackgroundIntensity = .055,
                TextWidthPx = 760
            },
            ReaderContentType.WebNovel => Base() with
            {
                ReadingMode = "continuous",
                PageTransition = "none",
                TwoPageSpread = false,
                ChapterStyle = "modern",
                GenreArtworkEnabled = true,
                BackgroundIntensity = .04,
                TextWidthPx = 820
            },
            ReaderContentType.Manga => Base() with
            {
                ReadingMode = "paged",
                TwoPageSpread = true,
                GenreArtworkEnabled = false,
                BackgroundIntensity = 0,
                BackgroundMotionMode = "static"
            },
            ReaderContentType.FixedDocument => Base() with
            {
                ReadingMode = "paged",
                PageTransition = "slide",
                TwoPageSpread = true,
                GenreArtworkEnabled = false,
                BackgroundIntensity = 0,
                BackgroundMotionMode = "static"
            },
            _ => Base() with
            {
                ReadingMode = "paged",
                ChapterStyle = "classic",
                PaperStyle = "cream",
                GenreArtworkEnabled = false,
                BackgroundIntensity = .025,
                TextWidthPx = 740
            }
        };

    private static ReaderSystemPreset Base() => new(
        ReadingMode: "continuous",
        PageTransition: "curl",
        TwoPageSpread: true,
        AutoScrollSpeed: 36,
        FontFamily: "literary-serif",
        FontSizeRem: 1.06,
        LineHeight: 1.9,
        ParagraphSpacingEm: .85,
        TextWidthPx: 760,
        TextAlignment: "start",
        ChapterStyle: "light-novel",
        PaperStyle: "auto",
        GenreArtworkEnabled: true,
        GenreTheme: "auto",
        BackgroundAssetId: "auto",
        BackgroundIntensity: .055,
        BackgroundMotionMode: "auto",
        ThemeEffectStrength: 1,
        ThemeBrightness: 1,
        ThemeContrast: 1,
        ThemeSaturation: 1,
        ThemeBlurPx: 0,
        ThemeVignetteStrength: 1,
        ThemeGrainStrength: 1,
        ThemeTextBackdropStrength: 1,
        ThemeParallaxStrength: 1,
        ThemeTintStrength: 1,
        BookmarkStyle: "fabric",
        BookmarkColor: "#b04455",
        TtsProviderId: "auto",
        TtsRate: 1,
        TtsPitch: 1,
        TtsVolume: 1,
        TtsAutoContinueChapters: false);
}
