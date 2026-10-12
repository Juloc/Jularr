using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    internal sealed record MediaPathInfo(string Title, int? Year, WorkUnitType? WorkUnitTypeId, decimal? UnitNumber, decimal? ParentUnitNumber);

    internal static partial class MediaPathParser
    {
        public static MediaPathInfo Parse(string relativePath, MediaType mediaType)
        {
            // Titles come from the top folder when present.
            var segments = relativePath.Split('/');
            var fileName = Path.GetFileNameWithoutExtension(segments[^1]);
            var title = Clean(segments.Length > 1 ? segments[0] : fileName, out var year);

            // Structure numbers come from the file and its folders.
            var folders = string.Join(' ', segments[..^1]);
            switch (mediaType)
            {
                case MediaType.Series:
                    if (SeasonEpisode().Match(fileName) is { Success: true } marked)
                    {
                        return new MediaPathInfo(title, year, WorkUnitType.Episode, Number(marked.Groups["episode"]), Number(marked.Groups["season"]));
                    }

                    var season = SeasonFolder().Match(folders) is { Success: true } folder ? Number(folder.Groups["number"]) : 1m;
                    return EpisodeOnly().Match(fileName) is { Success: true } episode
                        ? new MediaPathInfo(title, year, WorkUnitType.Episode, Number(episode.Groups["number"]), season)
                        : new MediaPathInfo(title, year, null, null, null);

                case MediaType.Book or MediaType.Manga or MediaType.LightNovel:
                    var volume = Volume().Match(fileName) is { Success: true } volumeMatch ? Number(volumeMatch.Groups["number"]) : (decimal?)null;
                    if (Chapter().Match(fileName) is { Success: true } chapter)
                    {
                        return new MediaPathInfo(title, year, WorkUnitType.Chapter, Number(chapter.Groups["number"]), volume);
                    }

                    return volume is null ? new MediaPathInfo(title, year, null, null, null) : new MediaPathInfo(title, year, WorkUnitType.Volume, volume, null);

                case MediaType.Music:
                    return LeadingTrack().Match(fileName) is { Success: true } track
                        ? new MediaPathInfo(title, year, WorkUnitType.Track, Number(track.Groups["number"]), null)
                        : new MediaPathInfo(title, year, null, null, null);

                default:
                    return new MediaPathInfo(title, year, null, null, null);
            }
        }

        public static bool IsSample(string relativePath) => Sample().IsMatch(Path.GetFileNameWithoutExtension(relativePath));

        private static string Clean(string name, out int? year)
        {
            // Release tags never belong to the title.
            var text = Brackets().Replace(name, " ");
            if (!text.Contains(' ')) text = text.Replace('.', ' ').Replace('_', ' ');

            year = null;
            var cut = text.Length;
            if (YearToken().Match(text) is { Success: true, Index: > 0 } yearMatch)
            {
                year = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
                cut = yearMatch.Index;
            }

            foreach (var marker in new[] { SeasonEpisode().Match(text), QualityToken().Match(text), Volume().Match(text), Chapter().Match(text) })
            {
                if (marker.Success && marker.Index > 0 && marker.Index < cut) cut = marker.Index;
            }

            var title = Whitespace().Replace(text[..cut], " ").Trim(' ', '-', '.', '_', '(', '[');
            return title.Length == 0 ? name.Trim() : title;
        }

        private static decimal Number(Group group) => decimal.Parse(group.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        [GeneratedRegex(@"[Ss](?<season>\d{1,3})[ ._-]*[Ee](?<episode>\d{1,4})|\b(?<season>\d{1,2})x(?<episode>\d{2,3})\b")]
        private static partial Regex SeasonEpisode();

        [GeneratedRegex(@"\b(?:season|staffel|s)[ ._-]*(?<number>\d{1,3})\b", RegexOptions.IgnoreCase)]
        private static partial Regex SeasonFolder();

        [GeneratedRegex(@"(?:\b(?:episode|folge|ep|e)[ ._-]*|[ ._]-[ ._])(?<number>\d{1,4})\b", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeOnly();

        [GeneratedRegex(@"\b(?:volume|vol|band|v)[ ._]*(?<number>\d{1,4}(?:\.\d{1,3})?)\b", RegexOptions.IgnoreCase)]
        private static partial Regex Volume();

        [GeneratedRegex(@"\b(?:chapter|kapitel|ch|c)[ ._]*(?<number>\d{1,4}(?:\.\d{1,3})?)\b", RegexOptions.IgnoreCase)]
        private static partial Regex Chapter();

        [GeneratedRegex(@"^(?:\d{1,2}-)?(?<number>\d{1,3})(?=[ ._-])")]
        private static partial Regex LeadingTrack();

        [GeneratedRegex(@"[\(\[ ._](?<year>(?:19|20)\d{2})(?=[\)\] ._]|$)")]
        private static partial Regex YearToken();

        [GeneratedRegex(@"\b(?:2160p|1080p|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|web-?dl|webrip|hdtv|dvdrip|x26[45]|h\.?26[45]|hevc|av1|remux)\b", RegexOptions.IgnoreCase)]
        private static partial Regex QualityToken();

        [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
        private static partial Regex Brackets();

        [GeneratedRegex(@"\bsample\b", RegexOptions.IgnoreCase)]
        private static partial Regex Sample();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
