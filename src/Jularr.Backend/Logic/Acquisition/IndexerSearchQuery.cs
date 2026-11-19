using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Logic.Acquisition
{
    // A free search as Prowlarr reads it: the words, and the tokens ({ImdbId:tt0111161}, {Season:01}) its type allows.
    // Each indexer gets the structured parameters it advertises; what it cannot take goes into the words where that still finds something.
    internal sealed partial class IndexerSearchQuery
    {
        // The tokens of each type and their Newznab parameter; a game's tokens only add words.
        private static readonly Dictionary<IndexerSearchType, (string Token, string Parameter)[]> Tokens = new()
        {
            [IndexerSearchType.Search] = [],
            [IndexerSearchType.Tv] = [("ImdbId", "imdbid"), ("TvdbId", "tvdbid"), ("TmdbId", "tmdbid"), ("TvMazeId", "tvmazeid"), ("Season", "season"), ("Episode", "ep")],
            [IndexerSearchType.Movie] = [("ImdbId", "imdbid"), ("TmdbId", "tmdbid"), ("Year", "year")],
            [IndexerSearchType.Music] = [("Artist", "artist"), ("Album", "album"), ("Label", "label")],
            [IndexerSearchType.Book] = [("Author", "author"), ("Title", "title")],
            [IndexerSearchType.Game] = [("Platform", "platform"), ("Year", "year")]
        };

        private IndexerSearchQuery(IndexerSearchType type, string text, IReadOnlyDictionary<string, string> parameters)
        {
            Type = type;
            Text = text;
            Parameters = parameters;
        }

        public IndexerSearchType Type { get; }

        // The words outside the tokens.
        public string Text { get; }

        // The Newznab parameters of the tokens, by their parameter name.
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public bool IsEmpty => Text.Length == 0 && Parameters.Count == 0;

        [GeneratedRegex(@"\{(\w+):([^{}]*)\}")]
        private static partial Regex Token();

        public static IndexerSearchQuery Parse(IndexerSearchType type, string? query)
        {
            var allowed = Tokens[type];
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = Token().Replace(query ?? "", match =>
            {
                var token = allowed.FirstOrDefault(pair => string.Equals(pair.Token, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                var value = match.Groups[2].Value.Trim();
                if (token.Parameter is not null && Value(token.Parameter, value) is { } parsed)
                {
                    parameters[token.Parameter] = parsed;
                }

                return " ";
            });
            return new IndexerSearchQuery(type, string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), parameters);
        }

        // The query one Newznab indexer is sent: its own search function and parameters where it has them, else the text search.
        public (NewznabSearchMode Mode, string Text, IReadOnlyList<KeyValuePair<string, string>> Parameters) For(NewznabCapabilities? capabilities)
        {
            var mode = Type switch
            {
                IndexerSearchType.Tv => NewznabSearchMode.TvSearch,
                IndexerSearchType.Movie => NewznabSearchMode.Movie,
                IndexerSearchType.Music => NewznabSearchMode.Music,
                IndexerSearchType.Book => NewznabSearchMode.Book,
                _ => NewznabSearchMode.Search
            };
            if (mode != NewznabSearchMode.Search && capabilities is not null && !capabilities.Supports(mode))
            {
                mode = NewznabSearchMode.Search;
            }

            var sent = Type == IndexerSearchType.Game ? [] : Parameters
                .Where(pair => mode != NewznabSearchMode.Search && (capabilities is null || capabilities.Supports(mode, pair.Key)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return (mode, WithWords(sent.Keys), [.. sent]);
        }

        // The query of an index that only reads words (the torrent index).
        public string Words() => WithWords([]);

        // The text, with the values the indexer was not sent as parameters where words can stand for them; identifiers cannot.
        private string WithWords(IEnumerable<string> sent)
        {
            var left = Parameters.Where(pair => !sent.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var words = new List<string> { Text };
            if (left.TryGetValue("season", out var season))
            {
                var number = int.Parse(season, CultureInfo.InvariantCulture);
                words.Add(left.TryGetValue("ep", out var episode)
                    ? "S" + number.ToString("00", CultureInfo.InvariantCulture) + "E" + int.Parse(episode, CultureInfo.InvariantCulture).ToString("00", CultureInfo.InvariantCulture)
                    : "S" + number.ToString("00", CultureInfo.InvariantCulture));
            }

            words.AddRange(new[] { "artist", "album", "label", "author", "title", "platform", "year" }.Where(left.ContainsKey).Select(key => left[key]));
            return string.Join(' ', words.Where(word => word.Length > 0));
        }

        // Ids and numbers as Newznab writes them (an IMDb id without "tt"); a value that is none is left out.
        private static string? Value(string parameter, string value) => parameter switch
        {
            "imdbid" => Regex.Match(value, @"^(?:tt)?(\d{1,10})$", RegexOptions.IgnoreCase) is { Success: true } imdb ? imdb.Groups[1].Value.PadLeft(7, '0') : null,
            "tvdbid" or "tmdbid" or "tvmazeid" or "season" or "ep" or "year" =>
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number.ToString(CultureInfo.InvariantCulture) : null,
            _ => value.Length is > 0 and <= 200 ? value : null
        };
    }
}
