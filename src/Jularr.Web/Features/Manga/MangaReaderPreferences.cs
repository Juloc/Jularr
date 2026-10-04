using Jularr.Web.Data;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;

namespace Jularr.Web.Features.Manga;

public sealed record MangaReaderPreset(
    string ReadingMode,
    bool TwoPageSpread,
    string PageTransition,
    string BookmarkColor,
    string ImageFlowMode,
    string PageDirection,
    string ImageFit,
    int ImageZoomPercent,
    int ImagePageGapPx,
    bool ImageFirstPageAlone,
    bool AutoContinueChapters,
    bool ImageSharpen,
    bool ImageCropBorders,
    string ImageColorScheme,
    bool HasSeriesOverride)
{
    public string UiMode =>
        ReadingMode == "continuous"
            ? ImageFlowMode
            : TwoPageSpread
                ? "double"
                : "single";
}

public sealed class MangaReaderPreferenceInput
{
    public string? Mode { get; set; }
    public string? PageDirection { get; set; }
    public string? ImageFit { get; set; }
    public int ImageZoomPercent { get; set; } = 100;
    public int ImagePageGapPx { get; set; } = 8;
    public bool ImageFirstPageAlone { get; set; }
    public bool AutoContinueChapters { get; set; } = true;
    public bool ImageSharpen { get; set; }
    public bool ImageCropBorders { get; set; }
    public string? ImageColorScheme { get; set; }
}

/// <summary>
/// Manga/comic UI mapping over the canonical ReaderPreferenceStore.
/// This adapter owns no persistence or inheritance rules.
/// </summary>
public static class MangaReaderPreferences
{
    private static readonly string[] FullSettingKeys =
    [
        "readingMode",
        "twoPageSpread",
        "imageFlowMode",
        "imagePageDirection",
        "imageFit",
        "imageZoomPercent",
        "imagePageGapPx",
        "imageFirstPageAlone",
        "autoContinueChapters",
        "imageSharpen",
        "imageCropBorders",
        "imageColorScheme"
    ];

    public static async Task<MangaReaderPreset> GetAsync(
        AppDbContext db,
        string profileId,
        Guid? workId,
        CancellationToken cancellationToken)
    {
        var settings = await ReaderPreferenceStore.GetAsync(
            db,
            profileId,
            workId,
            genresJson: null,
            ReaderContentType.Manga,
            cancellationToken);

        return FromSettings(settings);
    }

    public static async Task SaveAsync(
        AppDbContext db,
        string profileId,
        Guid? workId,
        MangaReaderPreferenceInput input,
        string? changedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var scopeKey = workId is Guid id
            ? ReaderPreferenceScopes.Work(id)
            : ReaderPreferenceScopes.Type(ReaderContentType.Manga);
        var settings = ToSettingsInput(input);

        if (string.IsNullOrWhiteSpace(changedKey))
        {
            await ReaderPreferenceStore.SaveScopeFieldsAsync(
                db,
                profileId,
                scopeKey,
                FullSettingKeys,
                settings,
                cancellationToken);
            return;
        }

        var keys = CanonicalKeys(changedKey);
        await ReaderPreferenceStore.SaveScopeFieldsAsync(
            db,
            profileId,
            scopeKey,
            keys,
            settings,
            cancellationToken);
    }

    public static Task ResetWorkAsync(
        AppDbContext db,
        string profileId,
        Guid workId,
        CancellationToken cancellationToken) =>
        ReaderPreferenceStore.ResetWorkAsync(
            db,
            profileId,
            workId,
            cancellationToken);

    private static MangaReaderPreset FromSettings(ReaderSettingsSnapshot settings) =>
        new(
            settings.ReadingMode,
            settings.TwoPageSpread,
            settings.PageTransition,
            settings.BookmarkColor,
            settings.ImageFlowMode,
            settings.ImagePageDirection,
            settings.ImageFit,
            settings.ImageZoomPercent,
            settings.ImagePageGapPx,
            settings.ImageFirstPageAlone,
            settings.AutoContinueChapters,
            settings.ImageSharpen,
            settings.ImageCropBorders,
            settings.ImageColorScheme,
            settings.HasWorkOverride);

    private static ReaderSettingsInput ToSettingsInput(MangaReaderPreferenceInput input)
    {
        var mode = NormalizeMode(input.Mode);
        return new ReaderSettingsInput
        {
            ReadingMode = mode is "single" or "double" ? "paged" : "continuous",
            TwoPageSpread = mode == "double",
            ImageFlowMode = mode is "continuous" or "horizontal" or "webtoon"
                ? mode
                : "continuous",
            ImagePageDirection = input.PageDirection,
            ImageFit = input.ImageFit,
            ImageZoomPercent = input.ImageZoomPercent,
            ImagePageGapPx = input.ImagePageGapPx,
            ImageFirstPageAlone = input.ImageFirstPageAlone,
            AutoContinueChapters = input.AutoContinueChapters,
            ImageSharpen = input.ImageSharpen,
            ImageCropBorders = input.ImageCropBorders,
            ImageColorScheme = input.ImageColorScheme
        };
    }

    private static IReadOnlyCollection<string> CanonicalKeys(string changedKey) =>
        changedKey.Trim() switch
        {
            "mode" => ["readingMode", "twoPageSpread", "imageFlowMode"],
            "pageDirection" => ["imagePageDirection"],
            "imageFit" => ["imageFit"],
            "imageZoomPercent" => ["imageZoomPercent"],
            "imagePageGapPx" => ["imagePageGapPx"],
            "imageFirstPageAlone" => ["imageFirstPageAlone"],
            "autoContinueChapters" => ["autoContinueChapters"],
            "imageSharpen" => ["imageSharpen"],
            "imageCropBorders" => ["imageCropBorders"],
            "imageColorScheme" => ["imageColorScheme"],
            _ => throw new InvalidOperationException("Unknown image reader setting.")
        };

    private static string NormalizeMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "double" => "double",
            "continuous" => "continuous",
            "horizontal" => "horizontal",
            "webtoon" => "webtoon",
            _ => "single"
        };
}
