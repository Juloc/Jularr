using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;

namespace Jularr.Backend.Logic.Library
{
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
                    // A file that represents several episodes or one daily broadcast must not be assigned to only its first parsed unit.
                    if (MultiEpisode().IsMatch(fileName) || DailyEpisode().IsMatch(fileName))
                    {
                        return new MediaPathInfo(title, year, null, null, null);
                    }

                    var folderSeasons = SeasonFolder().Matches(folders).Select(match => Number(match.Groups["number"])).Distinct().Take(2).ToArray();
                    if (folderSeasons.Length > 1)
                    {
                        return new MediaPathInfo(title, year, null, null, null);
                    }

                    if (SeasonEpisode().Match(fileName) is { Success: true } marked)
                    {
                        var seasonNumber = Number(marked.Groups["season"]);
                        if (folderSeasons.Length == 1 && folderSeasons[0] != seasonNumber)
                        {
                            return new MediaPathInfo(title, year, null, null, null);
                        }

                        return new MediaPathInfo(title, year, WorkUnitType.Episode, Number(marked.Groups["episode"]), seasonNumber);
                    }

                    var season = folderSeasons.Length == 1 ? folderSeasons[0] : 1m;
                    return EpisodeOnly().Match(fileName) is { Success: true } episode
                        ? new MediaPathInfo(title, year, WorkUnitType.Episode, Number(episode.Groups["number"]), season)
                        : new MediaPathInfo(title, year, null, null, null);

                case MediaType.Book:
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

                case MediaType.Game when segments.Length > 2:
                    return new MediaPathInfo(Clean(segments[1], out var gameYear), gameYear, null, null, null);

                default:
                    return new MediaPathInfo(title, year, null, null, null);
            }
        }

        // Conflicting ids must never produce a match.
        public static string? ImdbId(string relativePath)
        {
            var ids = ImdbToken().Matches(relativePath).Select(match => match.Value.ToLowerInvariant()).Distinct().Take(2).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }

        // All labelled ids in any path component must agree with themselves.
        public static (IReadOnlyDictionary<string, string> Ids, bool Conflicted) ProviderIds(string relativePath, MediaType mediaType)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var conflicted = false;

            void Add(string name, string value)
            {
                if (ids.TryGetValue(name, out var existing) && !existing.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    conflicted = true;
                }
                else
                {
                    ids[name] = value;
                }
            }

            foreach (Match match in ImdbToken().Matches(relativePath))
            {
                Add("imdb", match.Value.ToLowerInvariant());
            }

            foreach (Match match in TaggedId().Matches(relativePath))
            {
                var name = match.Groups["source"].Value.ToLowerInvariant() switch
                {
                    "tmdb" => mediaType == MediaType.Series ? "tmdb_tv" : "tmdb_movie",
                    "tvdb" => "tvdb",
                    "tvmaze" => "tvmaze",
                    "anilist" => "anilist",
                    "igdb" => "igdb",
                    _ => ""
                };
                if (name.Length > 0)
                {
                    Add(name, match.Groups["id"].Value);
                }
            }

            return (ids, conflicted);
        }

        // Read only work-level identifiers; episode NFO files cannot identify a whole series.
        public static (IReadOnlyDictionary<string, string> Ids, bool Conflicted) ParseNfo(string xml, MediaType mediaType)
        {
            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var conflicted = false;
            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 65_536
                });
                var document = XDocument.Load(reader);
                var expected = mediaType == MediaType.Series ? "tvshow" : "movie";
                if (!string.Equals(document.Root?.Name.LocalName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return (identities, true);
                }

                foreach (var entry in document.Root.Elements())
                {
                    var field = entry.Name.LocalName.ToLowerInvariant();
                    var source = field == "uniqueid" ? entry.Attribute("type")?.Value.ToLowerInvariant() : field;
                    var @namespace = source switch
                    {
                        "imdb" or "imdbid" => "imdb",
                        "tmdb" or "tmdbid" => mediaType == MediaType.Series ? "tmdb_tv" : "tmdb_movie",
                        "tvdb" or "tvdbid" => "tvdb",
                        "tvmaze" or "tvmazeid" => "tvmaze",
                        _ => null
                    };
                    if (@namespace is null)
                    {
                        continue;
                    }

                    var value = entry.Value.Trim();
                    var valid = !entry.HasElements && (@namespace == "imdb"
                        ? ImdbToken().Match(value) is { Success: true, Index: 0 } imdb && imdb.Length == value.Length
                        : value.Length is > 0 and <= 12 && value.All(char.IsAsciiDigit) && value.Any(digit => digit != '0'));
                    if (!valid || identities.TryGetValue(@namespace, out var previous) && !previous.Equals(value, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicted = true;
                        continue;
                    }

                    identities[@namespace] = @namespace == "imdb" ? value.ToLowerInvariant() : value;
                }
            }
            catch (XmlException)
            {
                conflicted = true;
            }

            return (identities, conflicted);
        }

        public static bool IsSample(string relativePath) => Sample().IsMatch(Path.GetFileNameWithoutExtension(relativePath));

        private static string Clean(string name, out int? year)
        {
            // Release tags never belong to the title.
            var text = TaggedIdSuffix().Replace(ImdbSuffix().Replace(Brackets().Replace(name, " "), " "), " ");
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

        [GeneratedRegex(@"(?:[Ss]\d{1,3}[ ._-]*[Ee]\d{1,4}(?:[ ._-]*[Ee]\d{1,4}|-\d{2,4})|\b\d{1,2}x\d{2,3}-\d{2,3}\b)")]
        private static partial Regex MultiEpisode();

        [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}[ ._-](?:0?[1-9]|1[0-2])[ ._-](?:0?[1-9]|[12]\d|3[01])(?!\d)")]
        private static partial Regex DailyEpisode();

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

        [GeneratedRegex(@"(?<![A-Za-z0-9])tt\d{7,10}(?!\d)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbToken();

        [GeneratedRegex(@"[\s._-]+(?:imdb(?:id)?[\s._:#-]*)?tt\d{7,10}\s*$", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbSuffix();

        [GeneratedRegex(@"(?<![A-Za-z0-9])(?<source>tmdb|tvdb|tvmaze|anilist|igdb)(?:id)?[\s._:#-]*(?<id>\d{1,12})(?!\d)", RegexOptions.IgnoreCase)]
        private static partial Regex TaggedId();

        [GeneratedRegex(@"[\s._-]+(?:tmdb|tvdb|tvmaze|anilist|igdb)(?:id)?[\s._:#-]*\d{1,12}\s*$", RegexOptions.IgnoreCase)]
        private static partial Regex TaggedIdSuffix();

        [GeneratedRegex(@"\bsample\b", RegexOptions.IgnoreCase)]
        private static partial Regex Sample();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
