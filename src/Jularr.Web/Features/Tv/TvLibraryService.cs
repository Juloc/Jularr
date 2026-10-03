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

        var series = await db.Set<TvSeries>().FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (series is null)
        {
            series = new TvSeries
            {
                Key = key,
                Title = cleanTitle,
                Year = year,
                TmdbId = Clean(tmdbId),
                TvdbId = Clean(tvdbId),
                LibraryPath = Clean(libraryPath)
            };
            db.Add(series);
        }
        else
        {
            series.Title = cleanTitle;
            series.Year ??= year;
            series.TmdbId ??= Clean(tmdbId);
            series.TvdbId ??= Clean(tvdbId);
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

    /// <summary>Upserts one episode of a series into the universal season/episode structure.</summary>
    public async Task<WorkEpisode> EnsureEpisodeAsync(
        Guid workId,
        int seasonNumber,
        int episodeNumber,
        string? episodeTitle,
        CancellationToken cancellationToken)
    {
        await EnsureEnabledAsync(cancellationToken);

        var season = await structure.AddOrUpdateSeasonAsync(workId, seasonNumber, title: null, cancellationToken);
        return await structure.AddOrUpdateEpisodeAsync(
            workId,
            seasonNumber,
            episodeNumber,
            absoluteNumber: null,
            isSpecial: seasonNumber == 0,
            title: string.IsNullOrWhiteSpace(episodeTitle) ? null : episodeTitle.Trim(),
            airedAt: null,
            seasonId: season.Id,
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
