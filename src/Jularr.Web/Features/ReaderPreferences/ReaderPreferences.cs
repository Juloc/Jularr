using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.Speech;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReaderPreferences;

public sealed class ReaderPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public string ScopeKey { get; set; } = "";

    public string? ReadingMode { get; set; }
    public string? PageTransition { get; set; }
    public bool? TwoPageSpread { get; set; }
    public double? AutoScrollSpeed { get; set; }

    public string? FontFamily { get; set; }
    public double? FontSizeRem { get; set; }
    public double? LineHeight { get; set; }
    public double? ParagraphSpacingEm { get; set; }
    public int? TextWidthPx { get; set; }
    public string? TextAlignment { get; set; }

    public string? ChapterStyle { get; set; }
    public string? PaperStyle { get; set; }
    public bool? GenreArtworkEnabled { get; set; }
    public string? GenreTheme { get; set; }
    public string? BackgroundAssetId { get; set; }
    public double? BackgroundIntensity { get; set; }
    public string? BackgroundMotionMode { get; set; }
    public double? ThemeEffectStrength { get; set; }
    public double? ThemeBrightness { get; set; }
    public double? ThemeContrast { get; set; }
    public double? ThemeSaturation { get; set; }
    public double? ThemeBlurPx { get; set; }
    public double? ThemeVignetteStrength { get; set; }
    public double? ThemeGrainStrength { get; set; }
    public double? ThemeTextBackdropStrength { get; set; }
    public double? ThemeParallaxStrength { get; set; }
    public double? ThemeTintStrength { get; set; }

    public string? BookmarkStyle { get; set; }
    public string? BookmarkColor { get; set; }

    /// <summary>
    /// Optional furigana over kanji, computed from the Japanese analysis
    /// pipeline for readers whose source has none of its own. Off by default;
    /// see <see cref="ReaderSettingsSnapshot.FuriganaEnabled"/>.
    /// </summary>
    public bool? FuriganaEnabled { get; set; }

    /// <summary>Hyphenate reflowable text (CSS hyphens).</summary>
    public bool? Hyphenation { get; set; }
    /// <summary>Show the page number in Pages mode.</summary>
    public bool? ShowPageNumbers { get; set; }
    /// <summary>Show embedded illustrations (EPUB images).</summary>
    public bool? ShowIllustrations { get; set; }
    /// <summary>Indent the first line of paragraphs.</summary>
    public bool? ParagraphIndent { get; set; }
    /// <summary>Turning past the last page (or scrolling to the end) opens the next chapter.</summary>
    public bool? AutoContinueChapters { get; set; }

    // Image-sequence presentation. These stay independent from text typography:
    // Manga/comic page flow and image treatment must not overload Book/Novel fields.
    public string? ImageFlowMode { get; set; }
    public string? ImagePageDirection { get; set; }
    public string? ImageFit { get; set; }
    public int? ImageZoomPercent { get; set; }
    public int? ImagePageGapPx { get; set; }
    public bool? ImageFirstPageAlone { get; set; }
    public bool? ImageSharpen { get; set; }
    public bool? ImageCropBorders { get; set; }
    public string? ImageColorScheme { get; set; }

    public string? TtsProviderId { get; set; }
    /// <summary>JSON object: normalized BCP-47 tag -> voice id. See <see cref="SpeechVoiceMap"/>.</summary>
    public string? TtsVoiceIds { get; set; }
    public double? TtsRate { get; set; }
    public double? TtsPitch { get; set; }
    public double? TtsVolume { get; set; }
    public bool? TtsAutoContinueChapters { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ReaderSettingsInput
{
    public string? ReadingMode { get; set; }
    public string? PageTransition { get; set; }
    public bool TwoPageSpread { get; set; }
    public double AutoScrollSpeed { get; set; }

    public string? FontFamily { get; set; }
    public double FontSizeRem { get; set; }
    public double LineHeight { get; set; }
    public double ParagraphSpacingEm { get; set; }
    public int TextWidthPx { get; set; }
    public string? TextAlignment { get; set; }

    public string? ChapterStyle { get; set; }
    public string? PaperStyle { get; set; }
    public bool GenreArtworkEnabled { get; set; }
    public string? GenreTheme { get; set; }
    public string? BackgroundAssetId { get; set; }
    public double BackgroundIntensity { get; set; }
    public string? BackgroundMotionMode { get; set; } = "auto";
    public double ThemeEffectStrength { get; set; } = 1;
    public double ThemeBrightness { get; set; } = 1;
    public double ThemeContrast { get; set; } = 1;
    public double ThemeSaturation { get; set; } = 1;
    public double ThemeBlurPx { get; set; }
    public double ThemeVignetteStrength { get; set; } = 1;
    public double ThemeGrainStrength { get; set; } = 1;
    public double ThemeTextBackdropStrength { get; set; } = 1;
    public double ThemeParallaxStrength { get; set; } = 1;
    public double ThemeTintStrength { get; set; } = 1;

    public string? BookmarkStyle { get; set; }
    public string? BookmarkColor { get; set; }

    public bool FuriganaEnabled { get; set; }

    // Nullable: a settings form that does not render one of these fields
    // (for example the Books reader) must not overwrite it with false.
    public bool? Hyphenation { get; set; }
    public bool? ShowPageNumbers { get; set; }
    public bool? ShowIllustrations { get; set; }
    public bool? ParagraphIndent { get; set; }
    public bool? AutoContinueChapters { get; set; }

    public string? ImageFlowMode { get; set; }
    public string? ImagePageDirection { get; set; }
    public string? ImageFit { get; set; }
    public int? ImageZoomPercent { get; set; }
    public int? ImagePageGapPx { get; set; }
    public bool? ImageFirstPageAlone { get; set; }
    public bool? ImageSharpen { get; set; }
    public bool? ImageCropBorders { get; set; }
    public string? ImageColorScheme { get; set; }

    public string? TtsProviderId { get; set; } = "auto";
    /// <summary>JSON object: language tag -> voice id (the effective map the client posts back).</summary>
    public string? TtsVoiceIds { get; set; }
    public double TtsRate { get; set; } = 1;
    public double TtsPitch { get; set; } = 1;
    public double TtsVolume { get; set; } = 1;
    public bool TtsAutoContinueChapters { get; set; }
}

public sealed record ReaderSettingsSnapshot(
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
    string ResolvedGenreTheme,
    IReadOnlyList<string> SourceGenres,
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
    bool HasWorkOverride)
{
    /// <summary>
    /// Off by default. The reader only offers the control when the content
    /// language's toolkit supports readings (Japanese today).
    /// </summary>
    public bool FuriganaEnabled { get; init; }

    public bool Hyphenation { get; init; } = true;
    public bool ShowPageNumbers { get; init; } = true;
    public bool ShowIllustrations { get; init; } = true;
    public bool ParagraphIndent { get; init; } = true;
    public bool AutoContinueChapters { get; init; }

    public string ImageFlowMode { get; init; } = "continuous";
    public string ImagePageDirection { get; init; } = "auto";
    public string ImageFit { get; init; } = "height";
    public int ImageZoomPercent { get; init; } = 100;
    public int ImagePageGapPx { get; init; } = 8;
    public bool ImageFirstPageAlone { get; init; }
    public bool ImageSharpen { get; init; }
    public bool ImageCropBorders { get; init; }
    public string ImageColorScheme { get; init; } = "auto";

    public string ContentTypeKey { get; init; } = "light-novel";
    public bool HasTypeOverride { get; init; }
    public bool HasGenreOverride { get; init; }
    public IReadOnlyDictionary<string, string> EffectiveSources { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string TtsProviderId { get; init; } = "auto";
    public IReadOnlyDictionary<string, string> TtsVoiceIds { get; init; } = SpeechVoiceMap.Empty;
    public double TtsRate { get; init; } = 1;
    public double TtsPitch { get; init; } = 1;
    public double TtsVolume { get; init; } = 1;
    public bool TtsAutoContinueChapters { get; init; }

    /// <summary>
    /// Builds the provider-neutral speech request for one document language from the
    /// effective Reader settings. This is the only bridge between Reader preferences and
    /// the speech resolver; the Reader never stores a second copy of these values.
    /// </summary>
    public SpeechPreferences SpeechPreferencesFor(string? language) =>
        new(
            ProviderId: string.Equals(TtsProviderId, "auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : TtsProviderId,
            VoiceId: SpeechVoiceMap.VoiceIdFor(TtsVoiceIds, language),
            Language: SpeechPreferenceResolver.NormalizeLanguageTag(language),
            Rate: TtsRate,
            Pitch: TtsPitch,
            Volume: TtsVolume);
}

public static partial class ReaderPreferenceRules
{
    public const string UserDefaultScope = "default";

    private static readonly HashSet<string> ReadingModes =
        new(StringComparer.OrdinalIgnoreCase) { "continuous", "paged" };
    private static readonly HashSet<string> PageTransitions =
        new(StringComparer.OrdinalIgnoreCase) { "curl", "slide", "fade", "none" };
    private static readonly HashSet<string> TextAlignments =
        new(StringComparer.OrdinalIgnoreCase) { "start", "justify" };
    private static readonly HashSet<string> ChapterStyles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "classic", "modern", "light-novel", "minimal", "decorative"
        };
    private static readonly HashSet<string> PaperStyles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "auto", "white", "cream", "sepia", "old-paper", "midnight", "oled"
        };
    private static readonly HashSet<string> BackgroundMotionModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "auto", "static", "parallax"
        };
    private static readonly HashSet<string> BookmarkStyles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "fabric", "paper", "leather", "cord", "minimal"
        };
    private static readonly HashSet<string> ImageFlowModes =
        new(StringComparer.OrdinalIgnoreCase) { "continuous", "horizontal", "webtoon" };
    private static readonly HashSet<string> ImagePageDirections =
        new(StringComparer.OrdinalIgnoreCase) { "auto", "ltr", "rtl" };
    private static readonly HashSet<string> ImageFits =
        new(StringComparer.OrdinalIgnoreCase) { "height", "width" };
    private static readonly HashSet<string> ImageColorSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "auto", "light", "sepia", "dark" };
    private static readonly HashSet<string> BuiltInFonts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "system-serif", "system-sans", "literary-serif", "book-serif",
            "atkinson", "noto-serif-jp", "noto-sans-jp"
        };

    public static string NormalizeReadingMode(string? value) =>
        NormalizeChoice(value, ReadingModes, "continuous");

    public static string NormalizePageTransition(string? value) =>
        NormalizeChoice(value, PageTransitions, "curl");

    public static string NormalizeTextAlignment(string? value) =>
        NormalizeChoice(value, TextAlignments, "start");

    public static string NormalizeChapterStyle(string? value) =>
        NormalizeChoice(value, ChapterStyles, "light-novel");

    public static string NormalizePaperStyle(string? value) =>
        NormalizeChoice(value, PaperStyles, "auto");

    public static string NormalizeBackgroundMotionMode(string? value) =>
        NormalizeChoice(value, BackgroundMotionModes, "auto");

    public static string NormalizeGenreTheme(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return normalized.Length <= 80 && CatalogKey().IsMatch(normalized)
            ? normalized.ToLowerInvariant()
            : "auto";
    }

    public static string NormalizeBookmarkStyle(string? value) =>
        NormalizeChoice(value, BookmarkStyles, "fabric");

    public static string NormalizeImageFlowMode(string? value) =>
        NormalizeChoice(value, ImageFlowModes, "continuous");

    public static string NormalizeImagePageDirection(string? value) =>
        NormalizeChoice(value, ImagePageDirections, "auto");

    public static string NormalizeImageFit(string? value) =>
        NormalizeChoice(value, ImageFits, "height");

    public static string NormalizeImageColorScheme(string? value) =>
        NormalizeChoice(value, ImageColorSchemes, "auto");

    public static int NormalizeImageZoomPercent(int value) =>
        Math.Clamp((int)Math.Round(value / 10d) * 10, 50, 300);

    public static int NormalizeImagePageGapPx(int value) =>
        Math.Clamp((int)Math.Round(value / 2d) * 2, 0, 48);

    public static string NormalizeBackgroundAssetId(string? value)
    {
        var normalized = value?.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return normalized.Length <= 120 && BackgroundAssetId().IsMatch(normalized)
            ? normalized.ToLowerInvariant()
            : "auto";
    }

    public static string NormalizeBookmarkColor(string? value)
    {
        var normalized = value?.Trim();
        return normalized is not null && HexColor().IsMatch(normalized)
            ? normalized.ToLowerInvariant()
            : "#b04455";
    }

    public static string NormalizeFontFamily(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "literary-serif";
        }

        if (BuiltInFonts.Contains(normalized))
        {
            return normalized.ToLowerInvariant();
        }

        if (normalized.StartsWith("google:", StringComparison.OrdinalIgnoreCase))
        {
            var family = normalized["google:".Length..].Trim();
            if (family.Length is > 0 and <= 80 && GoogleFontName().IsMatch(family))
            {
                return $"google:{family}";
            }
        }

        return "literary-serif";
    }

    public static double NormalizeAutoScrollSpeed(double value) =>
        Math.Round(Math.Clamp(value, 10, 180), 1);

    public static double NormalizeFontSize(double value) =>
        Math.Round(Math.Clamp(value, .85, 1.8), 2);

    public static double NormalizeLineHeight(double value) =>
        Math.Round(Math.Clamp(value, 1.4, 2.6), 2);

    public static double NormalizeParagraphSpacing(double value) =>
        Math.Round(Math.Clamp(value, 0, 2.2), 2);

    public static int NormalizeTextWidth(int value) =>
        Math.Clamp(value, 520, 1100);

    public static double NormalizeBackgroundIntensity(double value) =>
        Math.Round(Math.Clamp(value, 0, .12), 3);

    public static double NormalizeThemeEffectStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    public static double NormalizeThemeBrightness(double value) =>
        Math.Round(Math.Clamp(value, .65, 1.35), 2);

    public static double NormalizeThemeContrast(double value) =>
        Math.Round(Math.Clamp(value, .7, 1.3), 2);

    public static double NormalizeThemeSaturation(double value) =>
        Math.Round(Math.Clamp(value, 0, 1.5), 2);

    public static double NormalizeThemeBlurPx(double value) =>
        Math.Round(Math.Clamp(value, 0, 8), 1);

    public static double NormalizeThemeVignetteStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    public static double NormalizeThemeGrainStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    public static double NormalizeThemeTextBackdropStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    public static double NormalizeThemeParallaxStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    public static double NormalizeThemeTintStrength(double value) =>
        Math.Round(Math.Clamp(value, 0, 2), 2);

    private static readonly HashSet<string> TtsProviders =
        new(StringComparer.OrdinalIgnoreCase) { "auto", "device" };

    /// <summary>Field key prefix for one per-language voice entry: <c>ttsVoiceId:&lt;language&gt;</c>.</summary>
    public const string TtsVoiceFieldPrefix = "ttsVoiceId:";

    public static string NormalizeTtsProviderId(string? value) =>
        NormalizeChoice(value, TtsProviders, "auto");

    public static double NormalizeTtsRate(double value) =>
        SpeechPreferenceResolver.NormalizeRate(value);

    public static double NormalizeTtsPitch(double value) =>
        SpeechPreferenceResolver.NormalizePitch(value);

    public static double NormalizeTtsVolume(double value) =>
        SpeechPreferenceResolver.NormalizeVolume(value);

    public static string TtsVoiceFieldKey(string? language) =>
        TtsVoiceFieldPrefix + SpeechPreferenceResolver.NormalizeLanguageTag(language);

    public static bool TryParseTtsVoiceFieldKey(string? changedKey, out string language)
    {
        language = "";
        var key = changedKey?.Trim();
        if (key is null ||
            !key.StartsWith(TtsVoiceFieldPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = SpeechPreferenceResolver.NormalizeLanguageTag(key[TtsVoiceFieldPrefix.Length..]);
        return language != "und";
    }

    public static IReadOnlyList<string> ParseGenres(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json)
                ?.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }


    private static string NormalizeChoice(
        string? value,
        HashSet<string> allowed,
        string fallback)
    {
        var normalized = value?.Trim();
        return normalized is not null && allowed.Contains(normalized)
            ? normalized.ToLowerInvariant()
            : fallback;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogKey();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]*/[a-zA-Z0-9][a-zA-Z0-9_/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BackgroundAssetId();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    [GeneratedRegex("^[\\p{L}\\p{N} ._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleFontName();
}

public static class ReaderPreferenceStore
{
    public static Task<ReaderSettingsSnapshot> GetAsync(
        AppDbContext db,
        string profileId,
        Guid workId,
        string? genresJson,
        CancellationToken cancellationToken) =>
        GetAsync(
            db,
            profileId,
            workId,
            genresJson,
            ReaderContentType.LightNovel,
            cancellationToken);

    public static async Task<ReaderSettingsSnapshot> GetAsync(
        AppDbContext db,
        string profileId,
        Guid? workId,
        string? genresJson,
        ReaderContentType contentType,
        CancellationToken cancellationToken)
    {
        var preferences = await db.ReaderPreferences
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var genres = ReaderPreferenceRules.ParseGenres(genresJson);
        var genreKeys = genres
            .Select(ReaderPreferenceScopes.NormalizeGenreKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeScope = ReaderPreferenceScopes.Type(contentType);
        var workScope = workId is Guid id ? ReaderPreferenceScopes.Work(id) : null;

        var global = preferences.FirstOrDefault(
            x => x.ScopeKey == ReaderPreferenceRules.UserDefaultScope);
        var type = preferences.FirstOrDefault(
            x => x.ScopeKey.Equals(typeScope, StringComparison.OrdinalIgnoreCase));
        var work = workScope is null
            ? null
            : preferences.FirstOrDefault(
                x => x.ScopeKey.Equals(workScope, StringComparison.OrdinalIgnoreCase));

        var genreLayers = preferences
            .Select(preference =>
            {
                var matched = ReaderPreferenceScopes.TryParseGenre(
                    preference.ScopeKey,
                    out var priority,
                    out var genreKey);
                return new
                {
                    Preference = preference,
                    Matched = matched && genreKeys.Contains(genreKey),
                    Priority = priority,
                    GenreKey = genreKey
                };
            })
            .Where(x => x.Matched)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.GenreKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Preference)
            .ToArray();

        var layers = new List<ReaderPreference>();
        if (global is not null) layers.Add(global);
        if (type is not null) layers.Add(type);
        layers.AddRange(genreLayers);
        if (work is not null) layers.Add(work);

        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preset = ReaderPresetCatalog.For(contentType);

        string ResolveString(
            string key,
            Func<ReaderPreference, string?> selector,
            string fallback)
        {
            var value = fallback;
            sources[key] = "system";

            foreach (var layer in layers)
            {
                var candidate = selector(layer);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                value = candidate;
                sources[key] = layer.ScopeKey;
            }

            return value;
        }

        T ResolveValue<T>(
            string key,
            Func<ReaderPreference, T?> selector,
            T fallback)
            where T : struct
        {
            var value = fallback;
            sources[key] = "system";

            foreach (var layer in layers)
            {
                var candidate = selector(layer);
                if (candidate is null)
                {
                    continue;
                }

                value = candidate.Value;
                sources[key] = layer.ScopeKey;
            }

            return value;
        }

        var genreSelection = ReaderPreferenceRules.NormalizeGenreTheme(
            ResolveString("genreTheme", x => x.GenreTheme, preset.GenreTheme));

        // Per-language voice entries inherit independently: a higher layer that only
        // chooses a Japanese voice keeps the inherited German voice.
        var ttsVoiceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            foreach (var (language, voiceId) in SpeechVoiceMap.Parse(layer.TtsVoiceIds))
            {
                ttsVoiceIds[language] = voiceId;
                sources[ReaderPreferenceRules.TtsVoiceFieldKey(language)] = layer.ScopeKey;
            }
        }

        var snapshot = new ReaderSettingsSnapshot(
            ReadingMode: ReaderPreferenceRules.NormalizeReadingMode(
                ResolveString("readingMode", x => x.ReadingMode, preset.ReadingMode)),
            PageTransition: ReaderPreferenceRules.NormalizePageTransition(
                ResolveString("pageTransition", x => x.PageTransition, preset.PageTransition)),
            TwoPageSpread: ResolveValue(
                "twoPageSpread", x => x.TwoPageSpread, preset.TwoPageSpread),
            AutoScrollSpeed: ReaderPreferenceRules.NormalizeAutoScrollSpeed(
                ResolveValue("autoScrollSpeed", x => x.AutoScrollSpeed, preset.AutoScrollSpeed)),
            FontFamily: ReaderPreferenceRules.NormalizeFontFamily(
                ResolveString("fontFamily", x => x.FontFamily, preset.FontFamily)),
            FontSizeRem: ReaderPreferenceRules.NormalizeFontSize(
                ResolveValue("fontSizeRem", x => x.FontSizeRem, preset.FontSizeRem)),
            LineHeight: ReaderPreferenceRules.NormalizeLineHeight(
                ResolveValue("lineHeight", x => x.LineHeight, preset.LineHeight)),
            ParagraphSpacingEm: ReaderPreferenceRules.NormalizeParagraphSpacing(
                ResolveValue(
                    "paragraphSpacingEm",
                    x => x.ParagraphSpacingEm,
                    preset.ParagraphSpacingEm)),
            TextWidthPx: ReaderPreferenceRules.NormalizeTextWidth(
                ResolveValue("textWidthPx", x => x.TextWidthPx, preset.TextWidthPx)),
            TextAlignment: ReaderPreferenceRules.NormalizeTextAlignment(
                ResolveString(
                    "textAlignment",
                    x => x.TextAlignment,
                    preset.TextAlignment)),
            ChapterStyle: ReaderPreferenceRules.NormalizeChapterStyle(
                ResolveString(
                    "chapterStyle",
                    x => x.ChapterStyle,
                    preset.ChapterStyle)),
            PaperStyle: ReaderPreferenceRules.NormalizePaperStyle(
                ResolveString("paperStyle", x => x.PaperStyle, preset.PaperStyle)),
            GenreArtworkEnabled: ResolveValue(
                "genreArtworkEnabled",
                x => x.GenreArtworkEnabled,
                preset.GenreArtworkEnabled),
            GenreTheme: genreSelection,
            ResolvedGenreTheme: genreSelection,
            SourceGenres: genres,
            BackgroundAssetId: ReaderPreferenceRules.NormalizeBackgroundAssetId(
                ResolveString(
                    "backgroundAssetId",
                    x => x.BackgroundAssetId,
                    preset.BackgroundAssetId)),
            BackgroundIntensity: ReaderPreferenceRules.NormalizeBackgroundIntensity(
                ResolveValue(
                    "backgroundIntensity",
                    x => x.BackgroundIntensity,
                    preset.BackgroundIntensity)),
            BackgroundMotionMode: ReaderPreferenceRules.NormalizeBackgroundMotionMode(
                ResolveString(
                    "backgroundMotionMode",
                    x => x.BackgroundMotionMode,
                    preset.BackgroundMotionMode)),
            ThemeEffectStrength: ReaderPreferenceRules.NormalizeThemeEffectStrength(
                ResolveValue(
                    "themeEffectStrength",
                    x => x.ThemeEffectStrength,
                    preset.ThemeEffectStrength)),
            ThemeBrightness: ReaderPreferenceRules.NormalizeThemeBrightness(
                ResolveValue(
                    "themeBrightness",
                    x => x.ThemeBrightness,
                    preset.ThemeBrightness)),
            ThemeContrast: ReaderPreferenceRules.NormalizeThemeContrast(
                ResolveValue(
                    "themeContrast",
                    x => x.ThemeContrast,
                    preset.ThemeContrast)),
            ThemeSaturation: ReaderPreferenceRules.NormalizeThemeSaturation(
                ResolveValue(
                    "themeSaturation",
                    x => x.ThemeSaturation,
                    preset.ThemeSaturation)),
            ThemeBlurPx: ReaderPreferenceRules.NormalizeThemeBlurPx(
                ResolveValue("themeBlurPx", x => x.ThemeBlurPx, preset.ThemeBlurPx)),
            ThemeVignetteStrength: ReaderPreferenceRules.NormalizeThemeVignetteStrength(
                ResolveValue(
                    "themeVignetteStrength",
                    x => x.ThemeVignetteStrength,
                    preset.ThemeVignetteStrength)),
            ThemeGrainStrength: ReaderPreferenceRules.NormalizeThemeGrainStrength(
                ResolveValue(
                    "themeGrainStrength",
                    x => x.ThemeGrainStrength,
                    preset.ThemeGrainStrength)),
            ThemeTextBackdropStrength: ReaderPreferenceRules.NormalizeThemeTextBackdropStrength(
                ResolveValue(
                    "themeTextBackdropStrength",
                    x => x.ThemeTextBackdropStrength,
                    preset.ThemeTextBackdropStrength)),
            ThemeParallaxStrength: ReaderPreferenceRules.NormalizeThemeParallaxStrength(
                ResolveValue(
                    "themeParallaxStrength",
                    x => x.ThemeParallaxStrength,
                    preset.ThemeParallaxStrength)),
            ThemeTintStrength: ReaderPreferenceRules.NormalizeThemeTintStrength(
                ResolveValue(
                    "themeTintStrength",
                    x => x.ThemeTintStrength,
                    preset.ThemeTintStrength)),
            BookmarkStyle: ReaderPreferenceRules.NormalizeBookmarkStyle(
                ResolveString(
                    "bookmarkStyle",
                    x => x.BookmarkStyle,
                    preset.BookmarkStyle)),
            BookmarkColor: ReaderPreferenceRules.NormalizeBookmarkColor(
                ResolveString(
                    "bookmarkColor",
                    x => x.BookmarkColor,
                    preset.BookmarkColor)),
            HasWorkOverride: work is not null)
        {
            ContentTypeKey = ReaderContentTypes.ToKey(contentType),
            HasTypeOverride = type is not null,
            HasGenreOverride = genreLayers.Length > 0,
            // Off by default regardless of content-type preset; not part of
            // ReaderSystemPreset (Features/ReaderCore) since every preset agrees.
            FuriganaEnabled = ResolveValue("furiganaEnabled", x => x.FuriganaEnabled, false),
            Hyphenation = ResolveValue("hyphenation", x => x.Hyphenation, true),
            ShowPageNumbers = ResolveValue("showPageNumbers", x => x.ShowPageNumbers, true),
            ShowIllustrations = ResolveValue("showIllustrations", x => x.ShowIllustrations, true),
            ParagraphIndent = ResolveValue("paragraphIndent", x => x.ParagraphIndent, true),
            AutoContinueChapters = ResolveValue(
                "autoContinueChapters",
                x => x.AutoContinueChapters,
                preset.AutoContinueChapters),
            ImageFlowMode = ReaderPreferenceRules.NormalizeImageFlowMode(ResolveString("imageFlowMode", x => x.ImageFlowMode, "continuous")),
            ImagePageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(ResolveString("imagePageDirection", x => x.ImagePageDirection, "auto")),
            ImageFit = ReaderPreferenceRules.NormalizeImageFit(ResolveString("imageFit", x => x.ImageFit, "height")),
            ImageZoomPercent = ReaderPreferenceRules.NormalizeImageZoomPercent(ResolveValue("imageZoomPercent", x => x.ImageZoomPercent, 100)),
            ImagePageGapPx = ReaderPreferenceRules.NormalizeImagePageGapPx(ResolveValue("imagePageGapPx", x => x.ImagePageGapPx, 8)),
            ImageFirstPageAlone = ResolveValue("imageFirstPageAlone", x => x.ImageFirstPageAlone, false),
            ImageSharpen = ResolveValue("imageSharpen", x => x.ImageSharpen, false),
            ImageCropBorders = ResolveValue("imageCropBorders", x => x.ImageCropBorders, false),
            ImageColorScheme = ReaderPreferenceRules.NormalizeImageColorScheme(ResolveString("imageColorScheme", x => x.ImageColorScheme, "auto")),
            TtsProviderId = ReaderPreferenceRules.NormalizeTtsProviderId(
                ResolveString("ttsProviderId", x => x.TtsProviderId, preset.TtsProviderId)),
            TtsVoiceIds = ttsVoiceIds,
            TtsRate = ReaderPreferenceRules.NormalizeTtsRate(
                ResolveValue("ttsRate", x => x.TtsRate, preset.TtsRate)),
            TtsPitch = ReaderPreferenceRules.NormalizeTtsPitch(
                ResolveValue("ttsPitch", x => x.TtsPitch, preset.TtsPitch)),
            TtsVolume = ReaderPreferenceRules.NormalizeTtsVolume(
                ResolveValue("ttsVolume", x => x.TtsVolume, preset.TtsVolume)),
            TtsAutoContinueChapters = ResolveValue(
                "ttsAutoContinueChapters",
                x => x.TtsAutoContinueChapters,
                preset.TtsAutoContinueChapters),
            EffectiveSources = sources
        };

        return snapshot;
    }

    public static Task SaveUserDefaultsAsync(
        AppDbContext db,
        string profileId,
        ReaderSettingsInput input,
        CancellationToken cancellationToken) =>
        SaveScopeAsync(
            db,
            profileId,
            ReaderPreferenceRules.UserDefaultScope,
            input,
            cancellationToken);

    public static Task SaveTypeDefaultsAsync(
        AppDbContext db,
        string profileId,
        ReaderContentType contentType,
        ReaderSettingsInput input,
        CancellationToken cancellationToken) =>
        SaveScopeAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Type(contentType),
            input,
            cancellationToken);

    public static Task SaveGenreDefaultsAsync(
        AppDbContext db,
        string profileId,
        string genre,
        int priority,
        ReaderSettingsInput input,
        CancellationToken cancellationToken) =>
        SaveScopeAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Genre(genre, priority),
            input,
            cancellationToken);

    public static async Task SaveScopeAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        ReaderSettingsInput input,
        CancellationToken cancellationToken)
    {
        ValidateScope(scopeKey);
        var preference = await FindOrCreateAsync(db, profileId, scopeKey, cancellationToken);

        ApplyAll(preference, input);
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static Task SaveWorkOverrideAsync(
        AppDbContext db,
        string profileId,
        Guid workId,
        string changedKey,
        ReaderSettingsInput input,
        CancellationToken cancellationToken) =>
        SaveScopeFieldAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Work(workId),
            changedKey,
            input,
            cancellationToken);

    public static async Task SaveScopeFieldAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        string changedKey,
        ReaderSettingsInput input,
        CancellationToken cancellationToken)
    {
        ValidateScope(scopeKey);
        var preference = await FindOrCreateAsync(db, profileId, scopeKey, cancellationToken);

        ApplyField(preference, changedKey, input);
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task SaveScopeFieldsAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        IReadOnlyCollection<string> changedKeys,
        ReaderSettingsInput input,
        CancellationToken cancellationToken)
    {
        ValidateScope(scopeKey);
        if (changedKeys.Count == 0)
        {
            throw new ArgumentException("At least one reader setting is required.", nameof(changedKeys));
        }

        var preference = await FindOrCreateAsync(db, profileId, scopeKey, cancellationToken);
        foreach (var changedKey in changedKeys.Distinct(StringComparer.Ordinal))
        {
            ApplyField(preference, changedKey, input);
        }

        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task ResetScopeFieldAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        string changedKey,
        CancellationToken cancellationToken)
    {
        ValidateScope(scopeKey);
        var preference = await db.ReaderPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.ScopeKey == scopeKey,
                cancellationToken);

        if (preference is null)
        {
            return;
        }

        ClearField(preference, changedKey);
        preference.UpdatedAt = DateTime.UtcNow;

        if (IsEmpty(preference))
        {
            db.ReaderPreferences.Remove(preference);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static Task ResetWorkAsync(
        AppDbContext db,
        string profileId,
        Guid workId,
        CancellationToken cancellationToken) =>
        ResetScopeAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Work(workId),
            cancellationToken);

    public static Task ResetTypeAsync(
        AppDbContext db,
        string profileId,
        ReaderContentType contentType,
        CancellationToken cancellationToken) =>
        ResetScopeAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Type(contentType),
            cancellationToken);

    public static Task ResetGenreAsync(
        AppDbContext db,
        string profileId,
        string genre,
        int priority,
        CancellationToken cancellationToken) =>
        ResetScopeAsync(
            db,
            profileId,
            ReaderPreferenceScopes.Genre(genre, priority),
            cancellationToken);

    public static async Task ResetScopeAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        ValidateScope(scopeKey);
        var preference = await db.ReaderPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.ScopeKey == scopeKey,
                cancellationToken);

        if (preference is null)
        {
            return;
        }

        db.ReaderPreferences.Remove(preference);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyAll(
        ReaderPreference preference,
        ReaderSettingsInput input)
    {
        preference.ReadingMode =
            ReaderPreferenceRules.NormalizeReadingMode(input.ReadingMode);
        preference.PageTransition =
            ReaderPreferenceRules.NormalizePageTransition(input.PageTransition);
        preference.TwoPageSpread = input.TwoPageSpread;
        preference.AutoScrollSpeed =
            ReaderPreferenceRules.NormalizeAutoScrollSpeed(input.AutoScrollSpeed);
        preference.FontFamily =
            ReaderPreferenceRules.NormalizeFontFamily(input.FontFamily);
        preference.FontSizeRem =
            ReaderPreferenceRules.NormalizeFontSize(input.FontSizeRem);
        preference.LineHeight =
            ReaderPreferenceRules.NormalizeLineHeight(input.LineHeight);
        preference.ParagraphSpacingEm =
            ReaderPreferenceRules.NormalizeParagraphSpacing(input.ParagraphSpacingEm);
        preference.TextWidthPx =
            ReaderPreferenceRules.NormalizeTextWidth(input.TextWidthPx);
        preference.TextAlignment =
            ReaderPreferenceRules.NormalizeTextAlignment(input.TextAlignment);
        preference.ChapterStyle =
            ReaderPreferenceRules.NormalizeChapterStyle(input.ChapterStyle);
        preference.PaperStyle =
            ReaderPreferenceRules.NormalizePaperStyle(input.PaperStyle);
        preference.GenreArtworkEnabled = input.GenreArtworkEnabled;
        preference.GenreTheme =
            ReaderPreferenceRules.NormalizeGenreTheme(input.GenreTheme);
        preference.BackgroundAssetId =
            ReaderPreferenceRules.NormalizeBackgroundAssetId(input.BackgroundAssetId);
        preference.BackgroundIntensity =
            ReaderPreferenceRules.NormalizeBackgroundIntensity(input.BackgroundIntensity);
        preference.BackgroundMotionMode =
            ReaderPreferenceRules.NormalizeBackgroundMotionMode(input.BackgroundMotionMode);
        preference.ThemeEffectStrength =
            ReaderPreferenceRules.NormalizeThemeEffectStrength(input.ThemeEffectStrength);
        preference.ThemeBrightness =
            ReaderPreferenceRules.NormalizeThemeBrightness(input.ThemeBrightness);
        preference.ThemeContrast =
            ReaderPreferenceRules.NormalizeThemeContrast(input.ThemeContrast);
        preference.ThemeSaturation =
            ReaderPreferenceRules.NormalizeThemeSaturation(input.ThemeSaturation);
        preference.ThemeBlurPx =
            ReaderPreferenceRules.NormalizeThemeBlurPx(input.ThemeBlurPx);
        preference.ThemeVignetteStrength =
            ReaderPreferenceRules.NormalizeThemeVignetteStrength(input.ThemeVignetteStrength);
        preference.ThemeGrainStrength =
            ReaderPreferenceRules.NormalizeThemeGrainStrength(input.ThemeGrainStrength);
        preference.ThemeTextBackdropStrength =
            ReaderPreferenceRules.NormalizeThemeTextBackdropStrength(input.ThemeTextBackdropStrength);
        preference.ThemeParallaxStrength =
            ReaderPreferenceRules.NormalizeThemeParallaxStrength(input.ThemeParallaxStrength);
        preference.ThemeTintStrength =
            ReaderPreferenceRules.NormalizeThemeTintStrength(input.ThemeTintStrength);
        preference.BookmarkStyle =
            ReaderPreferenceRules.NormalizeBookmarkStyle(input.BookmarkStyle);
        preference.BookmarkColor =
            ReaderPreferenceRules.NormalizeBookmarkColor(input.BookmarkColor);
        preference.FuriganaEnabled = input.FuriganaEnabled;
        if (input.Hyphenation is bool hyphenationValue) preference.Hyphenation = hyphenationValue;
        if (input.ShowPageNumbers is bool showPageNumbersValue) preference.ShowPageNumbers = showPageNumbersValue;
        if (input.ShowIllustrations is bool showIllustrationsValue) preference.ShowIllustrations = showIllustrationsValue;
        if (input.ParagraphIndent is bool paragraphIndentValue) preference.ParagraphIndent = paragraphIndentValue;
        if (input.AutoContinueChapters is bool autoContinueChaptersValue) preference.AutoContinueChapters = autoContinueChaptersValue;
        if (input.ImageFlowMode is not null) preference.ImageFlowMode = ReaderPreferenceRules.NormalizeImageFlowMode(input.ImageFlowMode);
        if (input.ImagePageDirection is not null) preference.ImagePageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(input.ImagePageDirection);
        if (input.ImageFit is not null) preference.ImageFit = ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
        if (input.ImageZoomPercent is int imageZoomPercent) preference.ImageZoomPercent = ReaderPreferenceRules.NormalizeImageZoomPercent(imageZoomPercent);
        if (input.ImagePageGapPx is int imagePageGapPx) preference.ImagePageGapPx = ReaderPreferenceRules.NormalizeImagePageGapPx(imagePageGapPx);
        if (input.ImageFirstPageAlone is bool imageFirstPageAlone) preference.ImageFirstPageAlone = imageFirstPageAlone;
        if (input.ImageSharpen is bool imageSharpen) preference.ImageSharpen = imageSharpen;
        if (input.ImageCropBorders is bool imageCropBorders) preference.ImageCropBorders = imageCropBorders;
        if (input.ImageColorScheme is not null) preference.ImageColorScheme = ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
        preference.TtsProviderId =
            ReaderPreferenceRules.NormalizeTtsProviderId(input.TtsProviderId);
        preference.TtsVoiceIds =
            SpeechVoiceMap.Serialize(SpeechVoiceMap.Parse(input.TtsVoiceIds));
        preference.TtsRate =
            ReaderPreferenceRules.NormalizeTtsRate(input.TtsRate);
        preference.TtsPitch =
            ReaderPreferenceRules.NormalizeTtsPitch(input.TtsPitch);
        preference.TtsVolume =
            ReaderPreferenceRules.NormalizeTtsVolume(input.TtsVolume);
        preference.TtsAutoContinueChapters = input.TtsAutoContinueChapters;
    }

    private static void ApplyTtsVoice(
        ReaderPreference preference,
        string language,
        string? voiceId)
    {
        var map = new Dictionary<string, string>(
            SpeechVoiceMap.Parse(preference.TtsVoiceIds),
            StringComparer.OrdinalIgnoreCase);
        var normalizedVoice = SpeechVoiceMap.NormalizeVoiceId(voiceId);

        if (normalizedVoice is null)
        {
            map.Remove(language);
        }
        else if (map.Count < SpeechVoiceMap.MaxEntries || map.ContainsKey(language))
        {
            map[language] = normalizedVoice;
        }

        preference.TtsVoiceIds = SpeechVoiceMap.Serialize(map);
    }

    private static void ApplyField(
        ReaderPreference preference,
        string changedKey,
        ReaderSettingsInput input)
    {
        if (ReaderPreferenceRules.TryParseTtsVoiceFieldKey(changedKey, out var voiceLanguage))
        {
            // Only the exact language entry is written; base-language fallback is a read concern.
            SpeechVoiceMap.Parse(input.TtsVoiceIds).TryGetValue(voiceLanguage, out var voiceId);
            ApplyTtsVoice(preference, voiceLanguage, voiceId);
            return;
        }

        switch (changedKey.Trim())
        {
            case "readingMode":
                preference.ReadingMode =
                    ReaderPreferenceRules.NormalizeReadingMode(input.ReadingMode);
                break;
            case "pageTransition":
                preference.PageTransition =
                    ReaderPreferenceRules.NormalizePageTransition(input.PageTransition);
                break;
            case "twoPageSpread":
                preference.TwoPageSpread = input.TwoPageSpread;
                break;
            case "autoScrollSpeed":
                preference.AutoScrollSpeed =
                    ReaderPreferenceRules.NormalizeAutoScrollSpeed(input.AutoScrollSpeed);
                break;
            case "fontFamily":
                preference.FontFamily =
                    ReaderPreferenceRules.NormalizeFontFamily(input.FontFamily);
                break;
            case "fontSizeRem":
                preference.FontSizeRem =
                    ReaderPreferenceRules.NormalizeFontSize(input.FontSizeRem);
                break;
            case "lineHeight":
                preference.LineHeight =
                    ReaderPreferenceRules.NormalizeLineHeight(input.LineHeight);
                break;
            case "paragraphSpacingEm":
                preference.ParagraphSpacingEm =
                    ReaderPreferenceRules.NormalizeParagraphSpacing(input.ParagraphSpacingEm);
                break;
            case "textWidthPx":
                preference.TextWidthPx =
                    ReaderPreferenceRules.NormalizeTextWidth(input.TextWidthPx);
                break;
            case "textAlignment":
                preference.TextAlignment =
                    ReaderPreferenceRules.NormalizeTextAlignment(input.TextAlignment);
                break;
            case "chapterStyle":
                preference.ChapterStyle =
                    ReaderPreferenceRules.NormalizeChapterStyle(input.ChapterStyle);
                break;
            case "paperStyle":
                preference.PaperStyle =
                    ReaderPreferenceRules.NormalizePaperStyle(input.PaperStyle);
                break;
            case "genreArtworkEnabled":
                preference.GenreArtworkEnabled = input.GenreArtworkEnabled;
                break;
            case "genreTheme":
                preference.GenreTheme =
                    ReaderPreferenceRules.NormalizeGenreTheme(input.GenreTheme);
                break;
            case "backgroundAssetId":
                preference.BackgroundAssetId =
                    ReaderPreferenceRules.NormalizeBackgroundAssetId(input.BackgroundAssetId);
                break;
            case "backgroundIntensity":
                preference.BackgroundIntensity =
                    ReaderPreferenceRules.NormalizeBackgroundIntensity(input.BackgroundIntensity);
                break;
            case "backgroundMotionMode":
                preference.BackgroundMotionMode =
                    ReaderPreferenceRules.NormalizeBackgroundMotionMode(input.BackgroundMotionMode);
                break;
            case "themeEffectStrength":
                preference.ThemeEffectStrength =
                    ReaderPreferenceRules.NormalizeThemeEffectStrength(input.ThemeEffectStrength);
                break;
            case "themeBrightness":
                preference.ThemeBrightness =
                    ReaderPreferenceRules.NormalizeThemeBrightness(input.ThemeBrightness);
                break;
            case "themeContrast":
                preference.ThemeContrast =
                    ReaderPreferenceRules.NormalizeThemeContrast(input.ThemeContrast);
                break;
            case "themeSaturation":
                preference.ThemeSaturation =
                    ReaderPreferenceRules.NormalizeThemeSaturation(input.ThemeSaturation);
                break;
            case "themeBlurPx":
                preference.ThemeBlurPx =
                    ReaderPreferenceRules.NormalizeThemeBlurPx(input.ThemeBlurPx);
                break;
            case "themeVignetteStrength":
                preference.ThemeVignetteStrength =
                    ReaderPreferenceRules.NormalizeThemeVignetteStrength(input.ThemeVignetteStrength);
                break;
            case "themeGrainStrength":
                preference.ThemeGrainStrength =
                    ReaderPreferenceRules.NormalizeThemeGrainStrength(input.ThemeGrainStrength);
                break;
            case "themeTextBackdropStrength":
                preference.ThemeTextBackdropStrength =
                    ReaderPreferenceRules.NormalizeThemeTextBackdropStrength(input.ThemeTextBackdropStrength);
                break;
            case "themeParallaxStrength":
                preference.ThemeParallaxStrength =
                    ReaderPreferenceRules.NormalizeThemeParallaxStrength(input.ThemeParallaxStrength);
                break;
            case "themeTintStrength":
                preference.ThemeTintStrength =
                    ReaderPreferenceRules.NormalizeThemeTintStrength(input.ThemeTintStrength);
                break;
            case "bookmarkStyle":
                preference.BookmarkStyle =
                    ReaderPreferenceRules.NormalizeBookmarkStyle(input.BookmarkStyle);
                break;
            case "bookmarkColor":
                preference.BookmarkColor =
                    ReaderPreferenceRules.NormalizeBookmarkColor(input.BookmarkColor);
                break;
            case "furiganaEnabled":
                preference.FuriganaEnabled = input.FuriganaEnabled;
                break;
            case "hyphenation":
                preference.Hyphenation = input.Hyphenation ?? false;
                break;
            case "showPageNumbers":
                preference.ShowPageNumbers = input.ShowPageNumbers ?? false;
                break;
            case "showIllustrations":
                preference.ShowIllustrations = input.ShowIllustrations ?? false;
                break;
            case "paragraphIndent":
                preference.ParagraphIndent = input.ParagraphIndent ?? false;
                break;
            case "autoContinueChapters":
                preference.AutoContinueChapters = input.AutoContinueChapters ?? false;
                break;
            case "imageFlowMode":
                preference.ImageFlowMode = ReaderPreferenceRules.NormalizeImageFlowMode(input.ImageFlowMode);
                break;
            case "imagePageDirection":
                preference.ImagePageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(input.ImagePageDirection);
                break;
            case "imageFit":
                preference.ImageFit = ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
                break;
            case "imageZoomPercent":
                preference.ImageZoomPercent = ReaderPreferenceRules.NormalizeImageZoomPercent(input.ImageZoomPercent ?? 100);
                break;
            case "imagePageGapPx":
                preference.ImagePageGapPx = ReaderPreferenceRules.NormalizeImagePageGapPx(input.ImagePageGapPx ?? 8);
                break;
            case "imageFirstPageAlone":
                preference.ImageFirstPageAlone = input.ImageFirstPageAlone ?? false;
                break;
            case "imageSharpen":
                preference.ImageSharpen = input.ImageSharpen ?? false;
                break;
            case "imageCropBorders":
                preference.ImageCropBorders = input.ImageCropBorders ?? false;
                break;
            case "imageColorScheme":
                preference.ImageColorScheme = ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
                break;
            case "ttsProviderId":
                preference.TtsProviderId =
                    ReaderPreferenceRules.NormalizeTtsProviderId(input.TtsProviderId);
                break;
            case "ttsRate":
                preference.TtsRate =
                    ReaderPreferenceRules.NormalizeTtsRate(input.TtsRate);
                break;
            case "ttsPitch":
                preference.TtsPitch =
                    ReaderPreferenceRules.NormalizeTtsPitch(input.TtsPitch);
                break;
            case "ttsVolume":
                preference.TtsVolume =
                    ReaderPreferenceRules.NormalizeTtsVolume(input.TtsVolume);
                break;
            case "ttsAutoContinueChapters":
                preference.TtsAutoContinueChapters = input.TtsAutoContinueChapters;
                break;
            default:
                throw new InvalidOperationException("Unknown reader setting.");
        }
    }

    private static void ClearField(
        ReaderPreference preference,
        string changedKey)
    {
        if (ReaderPreferenceRules.TryParseTtsVoiceFieldKey(changedKey, out var voiceLanguage))
        {
            ApplyTtsVoice(preference, voiceLanguage, null);
            return;
        }

        switch (changedKey.Trim())
        {
            case "readingMode": preference.ReadingMode = null; break;
            case "pageTransition": preference.PageTransition = null; break;
            case "twoPageSpread": preference.TwoPageSpread = null; break;
            case "autoScrollSpeed": preference.AutoScrollSpeed = null; break;
            case "fontFamily": preference.FontFamily = null; break;
            case "fontSizeRem": preference.FontSizeRem = null; break;
            case "lineHeight": preference.LineHeight = null; break;
            case "paragraphSpacingEm": preference.ParagraphSpacingEm = null; break;
            case "textWidthPx": preference.TextWidthPx = null; break;
            case "textAlignment": preference.TextAlignment = null; break;
            case "chapterStyle": preference.ChapterStyle = null; break;
            case "paperStyle": preference.PaperStyle = null; break;
            case "genreArtworkEnabled": preference.GenreArtworkEnabled = null; break;
            case "genreTheme": preference.GenreTheme = null; break;
            case "backgroundAssetId": preference.BackgroundAssetId = null; break;
            case "backgroundIntensity": preference.BackgroundIntensity = null; break;
            case "backgroundMotionMode": preference.BackgroundMotionMode = null; break;
            case "themeEffectStrength": preference.ThemeEffectStrength = null; break;
            case "themeBrightness": preference.ThemeBrightness = null; break;
            case "themeContrast": preference.ThemeContrast = null; break;
            case "themeSaturation": preference.ThemeSaturation = null; break;
            case "themeBlurPx": preference.ThemeBlurPx = null; break;
            case "themeVignetteStrength": preference.ThemeVignetteStrength = null; break;
            case "themeGrainStrength": preference.ThemeGrainStrength = null; break;
            case "themeTextBackdropStrength": preference.ThemeTextBackdropStrength = null; break;
            case "themeParallaxStrength": preference.ThemeParallaxStrength = null; break;
            case "themeTintStrength": preference.ThemeTintStrength = null; break;
            case "bookmarkStyle": preference.BookmarkStyle = null; break;
            case "bookmarkColor": preference.BookmarkColor = null; break;
            case "furiganaEnabled": preference.FuriganaEnabled = null; break;
            case "hyphenation": preference.Hyphenation = null; break;
            case "showPageNumbers": preference.ShowPageNumbers = null; break;
            case "showIllustrations": preference.ShowIllustrations = null; break;
            case "paragraphIndent": preference.ParagraphIndent = null; break;
            case "autoContinueChapters": preference.AutoContinueChapters = null; break;
            case "imageFlowMode": preference.ImageFlowMode = null; break;
            case "imagePageDirection": preference.ImagePageDirection = null; break;
            case "imageFit": preference.ImageFit = null; break;
            case "imageZoomPercent": preference.ImageZoomPercent = null; break;
            case "imagePageGapPx": preference.ImagePageGapPx = null; break;
            case "imageFirstPageAlone": preference.ImageFirstPageAlone = null; break;
            case "imageSharpen": preference.ImageSharpen = null; break;
            case "imageCropBorders": preference.ImageCropBorders = null; break;
            case "imageColorScheme": preference.ImageColorScheme = null; break;
            case "ttsProviderId": preference.TtsProviderId = null; break;
            case "ttsRate": preference.TtsRate = null; break;
            case "ttsPitch": preference.TtsPitch = null; break;
            case "ttsVolume": preference.TtsVolume = null; break;
            case "ttsAutoContinueChapters": preference.TtsAutoContinueChapters = null; break;
            default:
                throw new InvalidOperationException("Unknown reader setting.");
        }
    }

    private static bool IsEmpty(ReaderPreference preference) =>
        preference.ReadingMode is null &&
        preference.PageTransition is null &&
        preference.TwoPageSpread is null &&
        preference.AutoScrollSpeed is null &&
        preference.FontFamily is null &&
        preference.FontSizeRem is null &&
        preference.LineHeight is null &&
        preference.ParagraphSpacingEm is null &&
        preference.TextWidthPx is null &&
        preference.TextAlignment is null &&
        preference.ChapterStyle is null &&
        preference.PaperStyle is null &&
        preference.GenreArtworkEnabled is null &&
        preference.GenreTheme is null &&
        preference.BackgroundAssetId is null &&
        preference.BackgroundIntensity is null &&
        preference.BackgroundMotionMode is null &&
        preference.ThemeEffectStrength is null &&
        preference.ThemeBrightness is null &&
        preference.ThemeContrast is null &&
        preference.ThemeSaturation is null &&
        preference.ThemeBlurPx is null &&
        preference.ThemeVignetteStrength is null &&
        preference.ThemeGrainStrength is null &&
        preference.ThemeTextBackdropStrength is null &&
        preference.ThemeParallaxStrength is null &&
        preference.ThemeTintStrength is null &&
        preference.BookmarkStyle is null &&
        preference.BookmarkColor is null &&
        preference.FuriganaEnabled is null &&
        preference.Hyphenation is null &&
        preference.ShowPageNumbers is null &&
        preference.ShowIllustrations is null &&
        preference.ParagraphIndent is null &&
        preference.AutoContinueChapters is null &&
        preference.ImageFlowMode is null &&
        preference.ImagePageDirection is null &&
        preference.ImageFit is null &&
        preference.ImageZoomPercent is null &&
        preference.ImagePageGapPx is null &&
        preference.ImageFirstPageAlone is null &&
        preference.ImageSharpen is null &&
        preference.ImageCropBorders is null &&
        preference.ImageColorScheme is null &&
        preference.TtsProviderId is null &&
        preference.TtsVoiceIds is null &&
        preference.TtsRate is null &&
        preference.TtsPitch is null &&
        preference.TtsVolume is null &&
        preference.TtsAutoContinueChapters is null;

    private static async Task<ReaderPreference> FindOrCreateAsync(
        AppDbContext db,
        string profileId,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var preference = await db.ReaderPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.ScopeKey == scopeKey,
                cancellationToken);

        if (preference is not null)
        {
            return preference;
        }

        preference = new ReaderPreference
        {
            ProfileId = profileId,
            ScopeKey = scopeKey
        };
        db.ReaderPreferences.Add(preference);
        return preference;
    }

    private static void ValidateScope(string scopeKey)
    {
        if (scopeKey == ReaderPreferenceRules.UserDefaultScope ||
            scopeKey.StartsWith("type:", StringComparison.OrdinalIgnoreCase) ||
            scopeKey.StartsWith("genre:", StringComparison.OrdinalIgnoreCase) ||
            scopeKey.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException("Unknown reader preference scope.");
    }

}

