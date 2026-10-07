using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Tv;

/// <summary>The series a completed download resolved to and the universal work it is bridged to.</summary>
public sealed record TvSeriesEntry(TvSeries Series, Guid WorkId);

/// <summary>
/// Creates and resolves first-class <see cref="TvSeries"/> records and bridges each to the universal media
/// core (#594). A series reuses the universal season/episode structure: <see cref="EnsureEpisodeAsync"/>
/// upserts a <c>WorkSeason</c>/<c>WorkEpisode</c> through <see cref="WorkStructureService"/> rather than a
/// per-type episode table. Idempotent on the series' <see cref="TvSeries.Key"/> and on each episode's
/// (season, episode) number.
/// </summary>
public sealed class TvLibraryService(
    AppDbContext db,
    LegacyWorkBridge bridge,
    WorkStructureService structure,
    IInstanceModuleService? instanceModules = null)
{
    public async Task<TvSeriesEntry> EnsureSeriesAsync(
        string title,
        int? year,
        string? tmdbId,
        string? tvdbId,
        string? libraryPath,
        CancellationToken cancellationToken)
    {
        await EnsureEnabledAsync(cancellationToken);

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var key = SeriesKey(cleanTitle, year);
        var cleanTmdbId = Clean(tmdbId);
        var cleanTvdbId = Clean(tvdbId);

        var series = await FindSeriesAsync(key, cleanTmdbId, cleanTvdbId, cancellationToken);
        if (series is null)
        {
            series = new TvSeries
            {
                Key = key,
                Title = cleanTitle,
                Year = year,
                TmdbId = cleanTmdbId,
                TvdbId = cleanTvdbId,
                LibraryPath = Clean(libraryPath)
            };
            db.Add(series);
        }
        else
        {
            series.Title = cleanTitle;
            series.Year ??= year;
            series.TmdbId ??= cleanTmdbId;
            series.TvdbId ??= cleanTvdbId;
            if (Clean(libraryPath) is { } path)
            {
                series.LibraryPath = path;
            }

            series.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        var workId = await bridge.EnsureWorkForSeriesAsync(series, cancellationToken);
        return new TvSeriesEntry(series, workId);
    }

    /// <summary>Whether the library already knows this series, without creating anything.</summary>
    public async Task<bool> SeriesExistsAsync(string title, int? year, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        return await FindSeriesAsync(SeriesKey(cleanTitle, year), Clean(tmdbId), Clean(tvdbId), cancellationToken) is not null;
    }

    private async Task<TvSeries?> FindSeriesAsync(string key, string? cleanTmdbId, string? cleanTvdbId, CancellationToken cancellationToken)
    {
        TvSeries? series = null;
        if (cleanTmdbId is not null)
        {
            series = await db.Set<TvSeries>().FirstOrDefaultAsync(x => x.TmdbId == cleanTmdbId, cancellationToken);
        }

        if (series is null && cleanTvdbId is not null)
        {
            series = await db.Set<TvSeries>().FirstOrDefaultAsync(x => x.TvdbId == cleanTvdbId, cancellationToken);
        }

        return series ?? await db.Set<TvSeries>().FirstOrDefaultAsync(x => x.Key == key && (cleanTmdbId == null || x.TmdbId == null) && (cleanTvdbId == null || x.TvdbId == null), cancellationToken);
    }

    /// <summary>
    /// Upserts one episode of a series into the universal season/episode structure. An import only knows the numbers (and at best a
    /// title from the release), so what the provider already recorded for the episode and its season (titles, air date, absolute
    /// number) is kept: the structure the Request was made against must not lose its facts when the file arrives.
    /// </summary>
    public async Task<WorkEpisode> EnsureEpisodeAsync(
        Guid workId,
        int seasonNumber,
        int episodeNumber,
        string? episodeTitle,
        CancellationToken cancellationToken)
    {
        await EnsureEnabledAsync(cancellationToken);

        var knownSeasonTitle = await db.Set<WorkSeason>().AsNoTracking()
            .Where(x => x.WorkId == workId && x.SeasonNumber == seasonNumber)
            .Select(x => x.Title)
            .FirstOrDefaultAsync(cancellationToken);
        var known = await db.Set<WorkEpisode>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.SeasonNumber == seasonNumber && x.EpisodeNumber == episodeNumber, cancellationToken);
        var season = await structure.AddOrUpdateSeasonAsync(workId, seasonNumber, knownSeasonTitle, cancellationToken);
        return await structure.AddOrUpdateEpisodeAsync(
            workId,
            seasonNumber,
            episodeNumber,
            known?.AbsoluteNumber,
            isSpecial: seasonNumber == 0,
            string.IsNullOrWhiteSpace(episodeTitle) ? known?.Title : episodeTitle.Trim(),
            known?.AiredAt,
            season.Id,
            cancellationToken);
    }

    private async Task EnsureEnabledAsync(CancellationToken cancellationToken)
    {
        if (instanceModules is not null
            && !await instanceModules.IsEnabledAsync(
                InstanceModule.Tv,
                cancellationToken))
        {
            throw new InvalidOperationException("TV module is disabled.");
        }
    }

    /// <summary>The stable de-duplication key of a series: folded title plus first-air year.</summary>
    public static string SeriesKey(string title, int? year)
    {
        var folded = new string((title ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return year is int value ? $"{folded}:{value}" : folded;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
