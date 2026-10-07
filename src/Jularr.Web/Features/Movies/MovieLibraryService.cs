using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Movies;

/// <summary>The movie a completed download resolved to and the universal work it is bridged to.</summary>
public sealed record MovieLibraryEntry(Movie Movie, Guid WorkId);

/// <summary>
/// Creates and resolves first-class <see cref="Movie"/> records and bridges each to the universal media
/// core (#593). Idempotent on the movie's <see cref="Movie.Key"/> (folded title + year) so a re-import of
/// the same movie refreshes the row and always resolves to the same <see cref="Work"/> through the
/// <c>WorkSourceKind.Movie</c> source link.
/// </summary>
public sealed class MovieLibraryService(
    AppDbContext db,
    LegacyWorkBridge bridge,
    IInstanceModuleService? instanceModules = null)
{
    public async Task<MovieLibraryEntry> EnsureAsync(
        string title,
        int? year,
        string? tmdbId,
        string? imdbId,
        string? libraryPath,
        CancellationToken cancellationToken)
    {
        if (instanceModules is not null
            && !await instanceModules.IsEnabledAsync(
                InstanceModule.Movie,
                cancellationToken))
        {
            throw new InvalidOperationException("Movie module is disabled.");
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var key = MovieKey(cleanTitle, year);
        var cleanTmdbId = Clean(tmdbId);
        var cleanImdbId = Clean(imdbId);

        var movie = await FindAsync(key, cleanTmdbId, cleanImdbId, cancellationToken);
        if (movie is null)
        {
            movie = new Movie
            {
                Key = key,
                Title = cleanTitle,
                Year = year,
                TmdbId = cleanTmdbId,
                ImdbId = cleanImdbId,
                LibraryPath = Clean(libraryPath)
            };
            db.Add(movie);
        }
        else
        {
            movie.Title = cleanTitle;
            movie.Year ??= year;
            movie.TmdbId ??= cleanTmdbId;
            movie.ImdbId ??= cleanImdbId;
            if (Clean(libraryPath) is { } path)
            {
                movie.LibraryPath = path;
            }

            movie.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        var workId = await bridge.EnsureWorkForMovieAsync(movie, cancellationToken);
        return new MovieLibraryEntry(movie, workId);
    }

    /// <summary>Whether the library already knows this movie, without creating anything.</summary>
    public async Task<bool> ExistsAsync(string title, int? year, string? tmdbId, string? imdbId, CancellationToken cancellationToken)
    {
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        return await FindAsync(MovieKey(cleanTitle, year), Clean(tmdbId), Clean(imdbId), cancellationToken) is not null;
    }

    private async Task<Movie?> FindAsync(string key, string? cleanTmdbId, string? cleanImdbId, CancellationToken cancellationToken)
    {
        Movie? movie = null;
        if (cleanTmdbId is not null)
        {
            movie = await db.Set<Movie>().FirstOrDefaultAsync(x => x.TmdbId == cleanTmdbId, cancellationToken);
        }

        if (movie is null && cleanImdbId is not null)
        {
            movie = await db.Set<Movie>().FirstOrDefaultAsync(x => x.ImdbId == cleanImdbId, cancellationToken);
        }

        return movie ?? await db.Set<Movie>().FirstOrDefaultAsync(x => x.Key == key && (cleanTmdbId == null || x.TmdbId == null) && (cleanImdbId == null || x.ImdbId == null), cancellationToken);
    }

    /// <summary>The stable de-duplication key of a movie: folded title plus year so remakes stay distinct.</summary>
    public static string MovieKey(string title, int? year)
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
