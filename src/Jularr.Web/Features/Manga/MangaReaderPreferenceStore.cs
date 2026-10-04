using Jularr.Web.Data;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Microsoft.EntityFrameworkCore;

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

public static class MangaReaderPreferenceStore
{
    private const string LegacyMediaScope = "media:manga";
    private static readonly string MediaScope =
        ReaderPreferenceScopes.Type(ReaderContentType.Manga);

    public static async Task<MangaReaderPreset> GetAsync(
        AppDbContext db,
        string profileId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var seriesScope = SeriesScope(seriesId);
        var preferences = await db.ReaderPreferences
            .AsNoTracking()
            .Where(x =>
                x.ProfileId == profileId &&
                (x.ScopeKey == ReaderPreferenceRules.UserDefaultScope ||
                 x.ScopeKey == LegacyMediaScope ||
                 x.ScopeKey == MediaScope ||
                 x.ScopeKey == seriesScope))
            .ToListAsync(cancellationToken);

        var user = preferences.FirstOrDefault(
            x => x.ScopeKey == ReaderPreferenceRules.UserDefaultScope);
        var legacyMedia = preferences.FirstOrDefault(
            x => x.ScopeKey == LegacyMediaScope);
        var media = preferences.FirstOrDefault(x => x.ScopeKey == MediaScope);
        var series = preferences.FirstOrDefault(x => x.ScopeKey == seriesScope);

        var readingMode = ReaderPreferenceRules.NormalizeReadingMode(
            First(
                series?.ReadingMode,
                media?.ReadingMode,
                legacyMedia?.ReadingMode,
                user?.ReadingMode,
                "paged"));
        var transition = ReaderPreferenceRules.NormalizePageTransition(
            First(
                series?.PageTransition,
                media?.PageTransition,
                legacyMedia?.PageTransition,
                user?.PageTransition,
                "slide"));
        var twoPageSpread =
            series?.TwoPageSpread
            ?? media?.TwoPageSpread
            ?? legacyMedia?.TwoPageSpread
            ?? user?.TwoPageSpread
            ?? true;
        var bookmarkColor = ReaderPreferenceRules.NormalizeBookmarkColor(
            First(
                series?.BookmarkColor,
                media?.BookmarkColor,
                legacyMedia?.BookmarkColor,
                user?.BookmarkColor,
                "#b04455"));

        return new MangaReaderPreset(
            readingMode,
            twoPageSpread,
            transition,
            bookmarkColor,
            ReaderPreferenceRules.NormalizeImageFlowMode(First(
                series?.ImageFlowMode,
                media?.ImageFlowMode,
                legacyMedia?.ImageFlowMode,
                user?.ImageFlowMode,
                "continuous")),
            ReaderPreferenceRules.NormalizeImagePageDirection(First(
                series?.ImagePageDirection,
                media?.ImagePageDirection,
                legacyMedia?.ImagePageDirection,
                user?.ImagePageDirection,
                "auto")),
            ReaderPreferenceRules.NormalizeImageFit(First(
                series?.ImageFit,
                media?.ImageFit,
                legacyMedia?.ImageFit,
                user?.ImageFit,
                "height")),
            ReaderPreferenceRules.NormalizeImageZoomPercent(
                series?.ImageZoomPercent
                ?? media?.ImageZoomPercent
                ?? legacyMedia?.ImageZoomPercent
                ?? user?.ImageZoomPercent
                ?? 100),
            ReaderPreferenceRules.NormalizeImagePageGapPx(
                series?.ImagePageGapPx
                ?? media?.ImagePageGapPx
                ?? legacyMedia?.ImagePageGapPx
                ?? user?.ImagePageGapPx
                ?? 8),
            series?.ImageFirstPageAlone
                ?? media?.ImageFirstPageAlone
                ?? legacyMedia?.ImageFirstPageAlone
                ?? user?.ImageFirstPageAlone
                ?? false,
            series?.AutoContinueChapters
                ?? media?.AutoContinueChapters
                ?? legacyMedia?.AutoContinueChapters
                ?? user?.AutoContinueChapters
                ?? true,
            series?.ImageSharpen
                ?? media?.ImageSharpen
                ?? legacyMedia?.ImageSharpen
                ?? user?.ImageSharpen
                ?? false,
            series?.ImageCropBorders
                ?? media?.ImageCropBorders
                ?? legacyMedia?.ImageCropBorders
                ?? user?.ImageCropBorders
                ?? false,
            ReaderPreferenceRules.NormalizeImageColorScheme(First(
                series?.ImageColorScheme,
                media?.ImageColorScheme,
                legacyMedia?.ImageColorScheme,
                user?.ImageColorScheme,
                "auto")),
            series is not null);
    }

    public static async Task SaveAsync(
        AppDbContext db,
        string profileId,
        Guid? seriesId,
        MangaReaderPreferenceInput input,
        string? changedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var preference = await FindOrCreateAsync(
            db,
            profileId,
            seriesId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(changedKey))
        {
            ApplyMode(preference, input.Mode);
            preference.ImagePageDirection =
                ReaderPreferenceRules.NormalizeImagePageDirection(input.PageDirection);
            preference.ImageFit =
                ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
            preference.ImageZoomPercent =
                ReaderPreferenceRules.NormalizeImageZoomPercent(input.ImageZoomPercent);
            preference.ImagePageGapPx =
                ReaderPreferenceRules.NormalizeImagePageGapPx(input.ImagePageGapPx);
            preference.ImageFirstPageAlone = input.ImageFirstPageAlone;
            preference.AutoContinueChapters = input.AutoContinueChapters;
            preference.ImageSharpen = input.ImageSharpen;
            preference.ImageCropBorders = input.ImageCropBorders;
            preference.ImageColorScheme =
                ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
        }
        else
        {
            ApplyField(preference, changedKey, input);
        }

        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task SaveModeAsync(
        AppDbContext db,
        string profileId,
        Guid? seriesId,
        string? uiMode,
        CancellationToken cancellationToken)
    {
        await SaveAsync(
            db,
            profileId,
            seriesId,
            new MangaReaderPreferenceInput { Mode = uiMode },
            changedKey: "mode",
            cancellationToken);
    }

    public static async Task ResetSeriesAsync(
        AppDbContext db,
        string profileId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var scope = SeriesScope(seriesId);
        var preference = await db.ReaderPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.ScopeKey == scope,
                cancellationToken);

        if (preference is null)
        {
            return;
        }

        db.ReaderPreferences.Remove(preference);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyField(
        ReaderPreference preference,
        string changedKey,
        MangaReaderPreferenceInput input)
    {
        switch (changedKey.Trim())
        {
            case "mode":
                ApplyMode(preference, input.Mode);
                break;
            case "pageDirection":
                preference.ImagePageDirection =
                    ReaderPreferenceRules.NormalizeImagePageDirection(input.PageDirection);
                break;
            case "imageFit":
                preference.ImageFit =
                    ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
                break;
            case "imageZoomPercent":
                preference.ImageZoomPercent =
                    ReaderPreferenceRules.NormalizeImageZoomPercent(input.ImageZoomPercent);
                break;
            case "imagePageGapPx":
                preference.ImagePageGapPx =
                    ReaderPreferenceRules.NormalizeImagePageGapPx(input.ImagePageGapPx);
                break;
            case "imageFirstPageAlone":
                preference.ImageFirstPageAlone = input.ImageFirstPageAlone;
                break;
            case "autoContinueChapters":
                preference.AutoContinueChapters = input.AutoContinueChapters;
                break;
            case "imageSharpen":
                preference.ImageSharpen = input.ImageSharpen;
                break;
            case "imageCropBorders":
                preference.ImageCropBorders = input.ImageCropBorders;
                break;
            case "imageColorScheme":
                preference.ImageColorScheme =
                    ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
                break;
            default:
                throw new InvalidOperationException("Unknown image reader setting.");
        }
    }

    private static void ApplyMode(
        ReaderPreference preference,
        string? uiMode)
    {
        var normalized = uiMode?.Trim().ToLowerInvariant() switch
        {
            "double" => "double",
            "continuous" => "continuous",
            "horizontal" => "horizontal",
            "webtoon" => "webtoon",
            _ => "single"
        };

        if (normalized is "single" or "double")
        {
            preference.ReadingMode = "paged";
            preference.TwoPageSpread = normalized == "double";
        }
        else
        {
            preference.ReadingMode = "continuous";
            preference.TwoPageSpread = false;
            preference.ImageFlowMode =
                ReaderPreferenceRules.NormalizeImageFlowMode(normalized);
        }

        preference.PageTransition ??= "slide";
    }

    private static async Task<ReaderPreference> FindOrCreateAsync(
        AppDbContext db,
        string profileId,
        Guid? seriesId,
        CancellationToken cancellationToken)
    {
        var scope = seriesId is Guid id
            ? SeriesScope(id)
            : MediaScope;

        var preference = await db.ReaderPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.ScopeKey == scope,
                cancellationToken);

        if (preference is not null)
        {
            return preference;
        }

        preference = new ReaderPreference
        {
            ProfileId = profileId,
            ScopeKey = scope,
            WorkId = null
        };
        db.ReaderPreferences.Add(preference);
        return preference;
    }

    private static string SeriesScope(Guid seriesId) =>
        $"media:manga:series:{seriesId:N}";

    private static string First(params string?[] values) =>
        values.First(x => !string.IsNullOrWhiteSpace(x))!;
}
