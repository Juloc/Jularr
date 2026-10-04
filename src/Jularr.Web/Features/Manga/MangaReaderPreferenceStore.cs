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
    public string UiMode => ReadingMode == "continuous" ? ImageFlowMode : TwoPageSpread ? "double" : "single";
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
    private static readonly string MediaScope = ReaderPreferenceScopes.Type(ReaderContentType.Manga);

    public static async Task<MangaReaderPreset> GetAsync(AppDbContext db, string profileId, Guid workId, Guid legacySeriesId, CancellationToken cancellationToken)
    {
        var workScope = ReaderPreferenceScopes.Work(workId);
        var legacySeriesScope = LegacySeriesScope(legacySeriesId);
        var preferences = await db.ReaderPreferences.AsNoTracking()
            .Where(x => x.ProfileId == profileId && (x.ScopeKey == ReaderPreferenceRules.UserDefaultScope || x.ScopeKey == LegacyMediaScope || x.ScopeKey == MediaScope || x.ScopeKey == workScope || x.ScopeKey == legacySeriesScope))
            .ToListAsync(cancellationToken);

        var user = preferences.FirstOrDefault(x => x.ScopeKey == ReaderPreferenceRules.UserDefaultScope);
        var legacyMedia = preferences.FirstOrDefault(x => x.ScopeKey == LegacyMediaScope);
        var media = preferences.FirstOrDefault(x => x.ScopeKey == MediaScope);
        var legacySeries = preferences.FirstOrDefault(x => x.ScopeKey == legacySeriesScope);
        var work = preferences.FirstOrDefault(x => x.ScopeKey == workScope);

        var readingMode = ReaderPreferenceRules.NormalizeReadingMode(First(work?.ReadingMode ?? legacySeries?.ReadingMode, media?.ReadingMode, legacyMedia?.ReadingMode, user?.ReadingMode, "paged"));
        var transition = ReaderPreferenceRules.NormalizePageTransition(First(work?.PageTransition ?? legacySeries?.PageTransition, media?.PageTransition, legacyMedia?.PageTransition, user?.PageTransition, "slide"));
        var twoPageSpread = work?.TwoPageSpread ?? legacySeries?.TwoPageSpread ?? media?.TwoPageSpread ?? legacyMedia?.TwoPageSpread ?? user?.TwoPageSpread ?? true;
        var bookmarkColor = ReaderPreferenceRules.NormalizeBookmarkColor(First(work?.BookmarkColor ?? legacySeries?.BookmarkColor, media?.BookmarkColor, legacyMedia?.BookmarkColor, user?.BookmarkColor, "#b04455"));
        var imageFlowMode = ReaderPreferenceRules.NormalizeImageFlowMode(First(work?.ImageFlowMode ?? legacySeries?.ImageFlowMode, media?.ImageFlowMode, legacyMedia?.ImageFlowMode, user?.ImageFlowMode, "continuous"));
        var pageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(First(work?.ImagePageDirection ?? legacySeries?.ImagePageDirection, media?.ImagePageDirection, legacyMedia?.ImagePageDirection, user?.ImagePageDirection, "auto"));
        var imageFit = ReaderPreferenceRules.NormalizeImageFit(First(work?.ImageFit ?? legacySeries?.ImageFit, media?.ImageFit, legacyMedia?.ImageFit, user?.ImageFit, "height"));
        var imageZoom = ReaderPreferenceRules.NormalizeImageZoomPercent(work?.ImageZoomPercent ?? legacySeries?.ImageZoomPercent ?? media?.ImageZoomPercent ?? legacyMedia?.ImageZoomPercent ?? user?.ImageZoomPercent ?? 100);
        var imageGap = ReaderPreferenceRules.NormalizeImagePageGapPx(work?.ImagePageGapPx ?? legacySeries?.ImagePageGapPx ?? media?.ImagePageGapPx ?? legacyMedia?.ImagePageGapPx ?? user?.ImagePageGapPx ?? 8);
        var firstPageAlone = work?.ImageFirstPageAlone ?? legacySeries?.ImageFirstPageAlone ?? media?.ImageFirstPageAlone ?? legacyMedia?.ImageFirstPageAlone ?? user?.ImageFirstPageAlone ?? false;
        var autoContinue = work?.AutoContinueChapters ?? legacySeries?.AutoContinueChapters ?? media?.AutoContinueChapters ?? legacyMedia?.AutoContinueChapters ?? user?.AutoContinueChapters ?? true;
        var sharpen = work?.ImageSharpen ?? legacySeries?.ImageSharpen ?? media?.ImageSharpen ?? legacyMedia?.ImageSharpen ?? user?.ImageSharpen ?? false;
        var crop = work?.ImageCropBorders ?? legacySeries?.ImageCropBorders ?? media?.ImageCropBorders ?? legacyMedia?.ImageCropBorders ?? user?.ImageCropBorders ?? false;
        var scheme = ReaderPreferenceRules.NormalizeImageColorScheme(First(work?.ImageColorScheme ?? legacySeries?.ImageColorScheme, media?.ImageColorScheme, legacyMedia?.ImageColorScheme, user?.ImageColorScheme, "auto"));

        return new MangaReaderPreset(readingMode, twoPageSpread, transition, bookmarkColor, imageFlowMode, pageDirection, imageFit, imageZoom, imageGap, firstPageAlone, autoContinue, sharpen, crop, scheme, work is not null || legacySeries is not null);
    }

    public static async Task SaveAsync(AppDbContext db, string profileId, Guid? workId, MangaReaderPreferenceInput input, string? changedKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var preference = await FindOrCreateAsync(db, profileId, workId, cancellationToken);

        if (string.IsNullOrWhiteSpace(changedKey))
        {
            ApplyMode(preference, input.Mode);
            preference.ImagePageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(input.PageDirection);
            preference.ImageFit = ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
            preference.ImageZoomPercent = ReaderPreferenceRules.NormalizeImageZoomPercent(input.ImageZoomPercent);
            preference.ImagePageGapPx = ReaderPreferenceRules.NormalizeImagePageGapPx(input.ImagePageGapPx);
            preference.ImageFirstPageAlone = input.ImageFirstPageAlone;
            preference.AutoContinueChapters = input.AutoContinueChapters;
            preference.ImageSharpen = input.ImageSharpen;
            preference.ImageCropBorders = input.ImageCropBorders;
            preference.ImageColorScheme = ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
        }
        else
        {
            ApplyField(preference, changedKey, input);
        }

        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static Task SaveModeAsync(AppDbContext db, string profileId, Guid? workId, string? uiMode, CancellationToken cancellationToken) =>
        SaveAsync(db, profileId, workId, new MangaReaderPreferenceInput { Mode = uiMode }, "mode", cancellationToken);

    public static async Task ResetWorkAsync(AppDbContext db, string profileId, Guid workId, Guid legacySeriesId, CancellationToken cancellationToken)
    {
        var workScope = ReaderPreferenceScopes.Work(workId);
        var legacySeriesScope = LegacySeriesScope(legacySeriesId);
        var preferences = await db.ReaderPreferences.Where(x => x.ProfileId == profileId && (x.ScopeKey == workScope || x.ScopeKey == legacySeriesScope)).ToListAsync(cancellationToken);
        if (preferences.Count == 0)
        {
            return;
        }

        db.ReaderPreferences.RemoveRange(preferences);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task MigrateLegacyScopesAsync(AppDbContext db, string profileId, Guid workId, Guid legacySeriesId, CancellationToken cancellationToken)
    {
        var workScope = ReaderPreferenceScopes.Work(workId);
        var legacySeriesScope = LegacySeriesScope(legacySeriesId);
        var scopes = new[] { LegacyMediaScope, MediaScope, legacySeriesScope, workScope };
        var preferences = await db.ReaderPreferences.Where(x => x.ProfileId == profileId && scopes.Contains(x.ScopeKey)).ToListAsync(cancellationToken);

        var legacyMedia = preferences.FirstOrDefault(x => x.ScopeKey == LegacyMediaScope);
        var media = preferences.FirstOrDefault(x => x.ScopeKey == MediaScope);
        if (legacyMedia is not null)
        {
            media ??= CreatePreference(db, profileId, MediaScope, workId: null);
            CopyMissingImageReaderFields(media, legacyMedia);
            db.ReaderPreferences.Remove(legacyMedia);
        }

        var legacySeries = preferences.FirstOrDefault(x => x.ScopeKey == legacySeriesScope);
        var work = preferences.FirstOrDefault(x => x.ScopeKey == workScope);
        if (legacySeries is not null)
        {
            work ??= CreatePreference(db, profileId, workScope, workId);
            CopyMissingImageReaderFields(work, legacySeries);
            db.ReaderPreferences.Remove(legacySeries);
        }

        if (legacyMedia is not null || legacySeries is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void ApplyField(ReaderPreference preference, string changedKey, MangaReaderPreferenceInput input)
    {
        switch (changedKey.Trim())
        {
            case "mode":
                ApplyMode(preference, input.Mode);
                break;
            case "pageDirection":
                preference.ImagePageDirection = ReaderPreferenceRules.NormalizeImagePageDirection(input.PageDirection);
                break;
            case "imageFit":
                preference.ImageFit = ReaderPreferenceRules.NormalizeImageFit(input.ImageFit);
                break;
            case "imageZoomPercent":
                preference.ImageZoomPercent = ReaderPreferenceRules.NormalizeImageZoomPercent(input.ImageZoomPercent);
                break;
            case "imagePageGapPx":
                preference.ImagePageGapPx = ReaderPreferenceRules.NormalizeImagePageGapPx(input.ImagePageGapPx);
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
                preference.ImageColorScheme = ReaderPreferenceRules.NormalizeImageColorScheme(input.ImageColorScheme);
                break;
            default:
                throw new InvalidOperationException("Unknown image reader setting.");
        }
    }

    private static void ApplyMode(ReaderPreference preference, string? uiMode)
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
            preference.ImageFlowMode = ReaderPreferenceRules.NormalizeImageFlowMode(normalized);
        }

        preference.PageTransition ??= "slide";
    }

    private static async Task<ReaderPreference> FindOrCreateAsync(AppDbContext db, string profileId, Guid? workId, CancellationToken cancellationToken)
    {
        var scope = workId is Guid id ? ReaderPreferenceScopes.Work(id) : MediaScope;
        var preference = await db.ReaderPreferences.SingleOrDefaultAsync(x => x.ProfileId == profileId && x.ScopeKey == scope, cancellationToken);
        return preference ?? CreatePreference(db, profileId, scope, workId);
    }

    private static ReaderPreference CreatePreference(AppDbContext db, string profileId, string scope, Guid? workId)
    {
        var preference = new ReaderPreference { ProfileId = profileId, ScopeKey = scope, WorkId = workId };
        db.ReaderPreferences.Add(preference);
        return preference;
    }

    private static void CopyMissingImageReaderFields(ReaderPreference target, ReaderPreference source)
    {
        target.ReadingMode ??= source.ReadingMode;
        target.PageTransition ??= source.PageTransition;
        target.TwoPageSpread ??= source.TwoPageSpread;
        target.BookmarkColor ??= source.BookmarkColor;
        target.ImageFlowMode ??= source.ImageFlowMode;
        target.ImagePageDirection ??= source.ImagePageDirection;
        target.ImageFit ??= source.ImageFit;
        target.ImageZoomPercent ??= source.ImageZoomPercent;
        target.ImagePageGapPx ??= source.ImagePageGapPx;
        target.ImageFirstPageAlone ??= source.ImageFirstPageAlone;
        target.AutoContinueChapters ??= source.AutoContinueChapters;
        target.ImageSharpen ??= source.ImageSharpen;
        target.ImageCropBorders ??= source.ImageCropBorders;
        target.ImageColorScheme ??= source.ImageColorScheme;
        target.UpdatedAt = DateTime.UtcNow;
    }

    private static string LegacySeriesScope(Guid seriesId) => $"media:manga:series:{seriesId:N}";

    private static string First(params string?[] values) => values.First(x => !string.IsNullOrWhiteSpace(x))!;
}
