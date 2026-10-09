using Jularr.Web.Data;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Acquisition.Naming;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Naming;

namespace Jularr.Web.Features.Acquisition.Import;

// Where an imported file goes. Problem is set when the file must not be imported automatically.
public sealed record AnimeImportTarget(
    string Directory,
    string Path,
    string? Problem);

/// <summary>
/// Builds the library path of an imported file with the canonical anime naming (#299): the
/// profile resolved by <see cref="AnimeNamingProfileStore"/> (anime, then library root, then
/// default), the anime's existing series folder (a new folder is named with the profile's series
/// folder template), the profile's season folder and episode template. Series, episode and
/// absolute-number inputs are built exactly like <see cref="AnimeRenameService"/> builds them, so
/// a later rename preview shows the imported file as unchanged. Release tokens come from the
/// shared release parser.
/// </summary>
public static class AnimeImportDestination
{
    public static AnimeImportTarget Build(
        AnimeNamingState naming,
        Anime anime,
        AnimeMetadata? metadata,
        IReadOnlyList<Episode> existingEpisodes,
        AnimeLibraryLocation location,
        IReadOnlyList<RequestedAnimeEpisode> targets,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(anime);
        ArgumentNullException.ThrowIfNull(location);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target episode is required.", nameof(targets));
        }

        var ordered = targets
            .OrderBy(target => target.SeasonNumber)
            .ThenBy(target => target.EpisodeNumber)
            .ToArray();
        var first = ordered[0];
        var resolution = AnimeNamingProfileStore.Resolve(naming, anime.Id, location.RootId);
        var profile = resolution.Profile;

        var seriesFolder = location.AnimeDirectory;
        if (seriesFolder is null)
        {
            var folderName = AnimeNamingFormatter.BuildSeriesFolderName(
                profile,
                AnimeRenameService.BuildSeries(anime, metadata, location.RootPath, resolution.SeriesType));
            seriesFolder = System.IO.Path.Combine(location.RootPath, folderName);
        }

        var series = AnimeRenameService.BuildSeries(anime, metadata, seriesFolder, resolution.SeriesType);

        // Absolute numbers come from local numbering including the episodes being imported.
        var episodes = existingEpisodes.ToList();
        foreach (var target in ordered.Where(target => !episodes.Any(episode =>
                     episode.SeasonNumber == target.SeasonNumber && episode.Number == target.EpisodeNumber)))
        {
            episodes.Add(new Episode { AnimeId = anime.Id, SeasonNumber = target.SeasonNumber, Number = target.EpisodeNumber, Title = "" });
        }

        var offsets = AnimeRenameService.LocalAbsoluteOffsets(episodes);
        var release = AnimeReleaseParser.TryParse(System.IO.Path.GetFileName(sourcePath), out var parsed) ? parsed : null;
        var namingEpisodes = ordered
            .Select(target => new AnimeNamingEpisode(
                target.SeasonNumber,
                target.EpisodeNumber,
                offsets.TryGetValue(target.SeasonNumber, out var offset) ? offset + target.EpisodeNumber : null,
                existingEpisodes.FirstOrDefault(episode =>
                        episode.SeasonNumber == target.SeasonNumber &&
                        episode.Number == target.EpisodeNumber &&
                        !string.IsNullOrWhiteSpace(episode.Title))?.Title
                    // The library scanner titles unknown episodes like this, so the name stays stable.
                    ?? $"Episode {target.EpisodeNumber}",
                release?.AirDate))
            .ToArray();

        var seasonFolder = AnimeNamingFormatter.BuildSeasonFolderName(profile, series, first.SeasonNumber);
        var fileName = AnimeNamingFormatter.BuildEpisodeFileName(
            profile,
            new AnimeNamingRequest(series, namingEpisodes, release)) + System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        var directory = seasonFolder is null ? seriesFolder : System.IO.Path.Combine(seriesFolder, seasonFolder);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, fileName));

        return new AnimeImportTarget(directory, path, Check(location, anime, first, seriesFolder, seasonFolder, fileName, path));
    }

    // A sidecar keeps everything after the video stem (for example ".ja.ass") behind the new stem.
    public static string BuildSidecarName(
        string videoSourceName,
        string sidecarSourceName,
        string importedVideoName)
    {
        var videoStem = System.IO.Path.GetFileNameWithoutExtension(videoSourceName);
        var suffix = sidecarSourceName.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase)
            ? sidecarSourceName[videoStem.Length..]
            : System.IO.Path.GetExtension(sidecarSourceName);

        return System.IO.Path.GetFileNameWithoutExtension(importedVideoName) + suffix;
    }

    private static string? Check(
        AnimeLibraryLocation location,
        Anime anime,
        RequestedAnimeEpisode first,
        string seriesFolder,
        string? seasonFolder,
        string fileName,
        string path)
    {
        var names = new[] { System.IO.Path.GetFileName(seriesFolder), seasonFolder, fileName }
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
        if (names.Any(name => string.IsNullOrWhiteSpace(name) || name is "." or ".." || NamingTemplateEngine.ExceedsNameLimit(name)))
        {
            return "The naming profile produces an empty or too long name for this file.";
        }

        var root = System.IO.Path.GetFullPath(location.RootPath).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return "The destination would leave the library root.";
        }

        // The library must scan the new file back to this anime and episode.
        if (!MediaPathParser.TryParse(location.RootPath, path, out var descriptor) ||
            !descriptor.AnimeKey.Equals(anime.Key, StringComparison.Ordinal) ||
            descriptor.SeasonNumber != first.SeasonNumber ||
            descriptor.EpisodeNumber != first.EpisodeNumber)
        {
            var parsedAs = descriptor is null
                ? "nothing"
                : $"{descriptor.AnimeKey} S{descriptor.SeasonNumber:00}E{descriptor.EpisodeNumber:00}";
            return $"The naming profile would name it '{System.IO.Path.GetFileName(path)}', which the library scans as {parsedAs} instead of {anime.Key} S{first.SeasonNumber:00}E{first.EpisodeNumber:00}; adjust the naming profile or import it manually.";
        }

        return null;
    }
}
