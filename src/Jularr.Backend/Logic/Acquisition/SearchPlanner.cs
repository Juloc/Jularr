using System.Globalization;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Logic.Acquisition
{
    // The one query planner of Automatic and Manual Search.
    internal static class SearchPlanner
    {
        private static readonly (string Key, string Parameter)[] TvIds = [("tvdb", "tvdbid"), ("tmdb", "tmdbid"), ("imdb", "imdbid")];
        private static readonly (string Key, string Parameter)[] MovieIds = [("imdb", "imdbid"), ("tmdb", "tmdbid")];
        private static readonly KeyValuePair<string, string>[] NoParameters = [];

        // The queries one indexer is asked, ordered by tier then ladder position and cut to the depth's variant budget.
        public static IReadOnlyList<PlannedQuery> Plan(SearchIntent intent, NewznabCapabilities? capabilities, SearchDepth depth)
        {
            ArgumentNullException.ThrowIfNull(intent);

            var budget = SearchBudget.For(depth);
            var titles = Titles(intent);
            if (titles.Count == 0)
            {
                throw new ArgumentException("A search needs at least one title.", nameof(intent));
            }

            var caps = capabilities ?? NewznabCapabilities.TextOnly(DateTimeOffset.MinValue);
            var rungs = new List<PlannedQuery>();
            switch (intent.Kind)
            {
                case AcquisitionKind.Movie:
                    PlanMovie(rungs, intent, titles, caps);
                    break;
                case AcquisitionKind.Tv:
                    PlanEpisodic(rungs, intent, titles, caps, structuredText: true);
                    break;
                case AcquisitionKind.Anime:
                    PlanEpisodic(rungs, intent, titles, caps, structuredText: false);
                    break;
                case AcquisitionKind.Book:
                case AcquisitionKind.Audiobook:
                    PlanBook(rungs, intent, titles, caps);
                    break;
                case AcquisitionKind.Manga:
                case AcquisitionKind.LightNovel:
                    PlanReading(rungs, intent, titles);
                    break;
                case AcquisitionKind.Music:
                    PlanMusic(rungs, intent, titles, caps);
                    break;
                default:
                    Add(rungs, "title", 0, NewznabSearchMode.Search, titles[0], NoParameters, titles[0]);
                    break;
            }

            // The category-less repeat of the strongest text query: many indexers file releases inconsistently. It is the last rung of the
            // expansion tier, so it only runs when the categorised queries found too little.
            if (rungs.FirstOrDefault(rung => rung.Mode == NewznabSearchMode.Search && rung.Text is not null) is { } first)
            {
                rungs.Add(new PlannedQuery("any-category", 1, first.Mode, first.Text, first.Parameters, $"{first.Provenance} · any category", true));
            }

            return [.. rungs
                .Where(rung => rung.Tier <= budget.MaxTier)
                .GroupBy(rung => rung.Key)
                .Select(group => group.First())
                .OrderBy(rung => rung.Tier)
                .ThenBy(rung => rungs.IndexOf(rung))
                .Take(budget.MaxQueryVariants)];
        }

        // The queries of a free-text search.
        public static IReadOnlyList<PlannedQuery> PlanText(IEnumerable<string> texts)
        {
            var rungs = new List<PlannedQuery>();
            foreach (var text in texts)
            {
                Add(rungs, "text", 0, NewznabSearchMode.Search, text, NoParameters, "Search text");
            }

            if (rungs.Count > 0)
            {
                rungs.Add(new PlannedQuery("any-category", 1, rungs[0].Mode, rungs[0].Text, rungs[0].Parameters, "Search text · any category", true));
            }

            return rungs.GroupBy(rung => rung.Key).Select(group => group.First()).ToArray();
        }

        // The strongest text query of a book.
        public static string BookQuery(string title, string? author) =>
            string.IsNullOrWhiteSpace(author) ? MainTitle(title) : $"{author.Trim()} {MainTitle(title)}";


        private static void PlanMovie(List<PlannedQuery> rungs, SearchIntent intent, IReadOnlyList<string> titles, NewznabCapabilities caps)
        {
            var year = intent.Year;
            if (caps.Supports(NewznabSearchMode.Movie))
            {
                foreach (var (index, (key, parameter)) in MovieIds.Index())
                {
                    if (intent.ExternalIds.TryGetValue(key, out var id) && caps.Supports(NewznabSearchMode.Movie, parameter) && NumericId(id) is { } numeric)
                    {
                        Add(rungs, "id", index == 0 ? 0 : 1, NewznabSearchMode.Movie, null, [new(parameter, numeric)], $"{key.ToUpperInvariant()} ID");
                    }
                }
            }

            var withYear = year is int y ? $"{titles[0]} {y.ToString(CultureInfo.InvariantCulture)}" : titles[0];
            Add(rungs, "title", 0, NewznabSearchMode.Search, withYear, NoParameters, year is null ? "Title" : "Title + year");
            foreach (var alias in titles.Skip(1).Take(2))
            {
                Add(rungs, "alias", 1, NewznabSearchMode.Search, year is int a ? $"{alias} {a.ToString(CultureInfo.InvariantCulture)}" : alias, NoParameters, year is null ? "Alias" : "Alias + year");
            }

            if (year is not null)
            {
                Add(rungs, "fallback", 1, NewznabSearchMode.Search, titles[0], NoParameters, "Title");
            }

            foreach (var alias in titles.Skip(3))
            {
                Add(rungs, "alias", 2, NewznabSearchMode.Search, alias, NoParameters, "Alias");
            }
        }

        private static void PlanEpisodic(List<PlannedQuery> rungs, SearchIntent intent, IReadOnlyList<string> titles, NewznabCapabilities caps, bool structuredText)
        {
            var season = intent.Season;
            var episode = intent.Episode;
            var hasEpisode = season is >= 0 && episode is > 0;
            var tvSearch = caps.Supports(NewznabSearchMode.TvSearch);
            var numbering = new List<KeyValuePair<string, string>>();
            if (season is >= 0 && caps.Supports(NewznabSearchMode.TvSearch, "season"))
            {
                numbering.Add(new("season", season.Value.ToString(CultureInfo.InvariantCulture)));
                if (hasEpisode && caps.Supports(NewznabSearchMode.TvSearch, "ep"))
                {
                    numbering.Add(new("ep", episode!.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // Structured IDs: the strongest evidence first; a second ID only joins in the expansion tier.
            var numberingText = hasEpisode ? $"S{season!.Value:00}E{episode!.Value:00}" : season is >= 0 ? $"S{season.Value:00}" : null;
            if (tvSearch && numbering.Count > 0)
            {
                var emitted = 0;
                foreach (var (key, parameter) in TvIds)
                {
                    if (intent.ExternalIds.TryGetValue(key, out var id) && caps.Supports(NewznabSearchMode.TvSearch, parameter) && NumericId(id) is { } numeric)
                    {
                        Add(rungs, "id", emitted++ == 0 ? 0 : 1, NewznabSearchMode.TvSearch, null, [new(parameter, numeric), .. numbering], $"{key.ToUpperInvariant()}{(numberingText is null ? string.Empty : $" + {numberingText}")}");
                    }
                }
            }

            var canonical = titles[0];
            AddNumbered(rungs, intent, canonical, 0, structuredText && tvSearch && numbering.Count > 0 ? numbering : null, numberingText);

            foreach (var alias in titles.Skip(1).Take(2))
            {
                AddNumbered(rungs, intent, alias, 1, structuredText && tvSearch && numbering.Count > 0 ? numbering : null, numberingText);
            }

            foreach (var alias in titles.Skip(3))
            {
                AddNumbered(rungs, intent, alias, 2, null, numberingText);
            }
        }

        // One title with the numbering forms the target has. An episode is asked as SxxEyy first; the absolute number follows for Anime
        // because releases of long-running series carry only that.
        private static void AddNumbered(List<PlannedQuery> rungs, SearchIntent intent, string title, int tier, IReadOnlyList<KeyValuePair<string, string>>? structured, string? numberingText)
        {
            var stage = tier == 0 ? "title" : "alias";
            var label = tier == 0 ? "Title" : "Alias";
            var season = intent.Season;
            var episode = intent.Episode;
            if (season is >= 0 && episode is > 0)
            {
                if (structured is not null)
                {
                    Add(rungs, stage, tier, NewznabSearchMode.TvSearch, title, structured, $"{label} + {numberingText}");
                }

                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} {numberingText}", NoParameters, $"{label} + {numberingText}");
            }
            else if (season is >= 0)
            {
                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} S{season.Value:00}", NoParameters, $"{label} + S{season.Value:00}");
                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} Season {season.Value}", NoParameters, $"{label} + Season {season.Value}");
            }

            if (intent.AbsoluteEpisode is > 0 and var absolute)
            {
                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} - {absolute:00}", NoParameters, $"{label} + absolute {absolute}");
                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} {absolute:00}", NoParameters, $"{label} + absolute {absolute}");
            }
            else if (intent.Kind == AcquisitionKind.Anime && episode is > 0 and var number)
            {
                Add(rungs, stage, tier, NewznabSearchMode.Search, $"{title} {number:00}", NoParameters, $"{label} + episode {number}");
            }

            if (season is null && episode is null && intent.AbsoluteEpisode is null)
            {
                Add(rungs, stage, tier, NewznabSearchMode.Search, title, NoParameters, label);
            }
        }

        private static void PlanBook(List<PlannedQuery> rungs, SearchIntent intent, IReadOnlyList<string> titles, NewznabCapabilities caps)
        {
            var full = titles[0];
            var main = MainTitle(full);
            var creator = string.IsNullOrWhiteSpace(intent.Creator) ? null : intent.Creator.Trim();
            var bookSearch = caps.Supports(NewznabSearchMode.Book) && caps.Supports(NewznabSearchMode.Book, "title");
            if (bookSearch && creator is not null && caps.Supports(NewznabSearchMode.Book, "author"))
            {
                Add(rungs, "title", 0, NewznabSearchMode.Book, null, [new("title", main), new("author", creator)], "Title + author (book search)");
            }

            if (creator is not null)
            {
                Add(rungs, "title", 0, NewznabSearchMode.Search, BookQuery(full, creator), NoParameters, "Author + title");
            }

            Add(rungs, "title", 0, NewznabSearchMode.Search, main, NoParameters, "Title");
            if (!main.Equals(full, StringComparison.OrdinalIgnoreCase))
            {
                Add(rungs, "title", 1, NewznabSearchMode.Search, full, NoParameters, "Full title");
            }

            foreach (var alias in titles.Skip(1))
            {
                var aliasMain = MainTitle(alias);
                Add(rungs, "alias", 1, NewznabSearchMode.Search, creator is null ? aliasMain : $"{creator} {aliasMain}", NoParameters, "Alias");
            }
        }

        private static void PlanMusic(List<PlannedQuery> rungs, SearchIntent intent, IReadOnlyList<string> titles, NewznabCapabilities caps)
        {
            var artist = string.IsNullOrWhiteSpace(intent.Creator) ? null : Normalize(intent.Creator);
            var album = titles[0];
            var year = intent.Year?.ToString(CultureInfo.InvariantCulture);
            if (artist is not null && caps.Supports(NewznabSearchMode.Music, "artist") && caps.Supports(NewznabSearchMode.Music, "album"))
            {
                var parameters = new List<KeyValuePair<string, string>> { new("artist", artist), new("album", album) };
                if (year is not null && caps.Supports(NewznabSearchMode.Music, "year"))
                {
                    parameters.Add(new("year", year));
                }

                Add(rungs, "title", 0, NewznabSearchMode.Music, null, parameters, "Artist + album (music search)");
            }

            Add(rungs, "title", 0, NewznabSearchMode.Search, artist is null ? album : $"{artist} {album}", NoParameters, artist is null ? "Album" : "Artist + album");
            if (artist is not null)
            {
                Add(rungs, "title", 1, NewznabSearchMode.Search, $"{artist} {album} FLAC", NoParameters, "Artist + album + FLAC");
                if (year is not null)
                {
                    Add(rungs, "title", 1, NewznabSearchMode.Search, $"{artist} {album} {year}", NoParameters, "Artist + album + year");
                }
            }

            foreach (var alias in titles.Skip(1).Take(3))
            {
                Add(rungs, "alias", 1, NewznabSearchMode.Search, artist is null ? alias : $"{artist} {alias}", NoParameters, "Artist + alternate album title");
            }

            // Without the artist the search finds compilations and covers: only the deepest search tries it.
            if (artist is not null)
            {
                Add(rungs, "fallback", 2, NewznabSearchMode.Search, album, NoParameters, "Album title only");
            }
        }

        private static void PlanReading(List<PlannedQuery> rungs, SearchIntent intent, IReadOnlyList<string> titles)
        {
            var creator = string.IsNullOrWhiteSpace(intent.Creator) ? null : intent.Creator.Trim();
            foreach (var (index, name) in titles.Take(4).Index())
            {
                var tier = index == 0 ? 0 : 1;
                var stage = index == 0 ? "title" : "alias";
                if (creator is not null && intent.Kind == AcquisitionKind.LightNovel)
                {
                    Add(rungs, stage, tier, NewznabSearchMode.Search, $"{creator} {name}", NoParameters, "Author + title");
                }

                if (intent.Volume is { } volume)
                {
                    Add(rungs, stage, tier, NewznabSearchMode.Search, $"{name} vol {volume}", NoParameters, $"Title + volume {volume}");
                    Add(rungs, stage, tier, NewznabSearchMode.Search, $"{name} volume {volume}", NoParameters, $"Title + volume {volume}");
                }
                else if (intent.Chapter is { } chapter)
                {
                    Add(rungs, stage, tier, NewznabSearchMode.Search, $"{name} ch {chapter.ToString(CultureInfo.InvariantCulture)}", NoParameters, $"Title + chapter {chapter.ToString(CultureInfo.InvariantCulture)}");
                }

                Add(rungs, stage, tier, NewznabSearchMode.Search, name, NoParameters, index == 0 ? "Title" : "Alias");
            }

            foreach (var alias in titles.Skip(4))
            {
                Add(rungs, "alias", 2, NewznabSearchMode.Search, alias, NoParameters, "Alias");
            }
        }

        private static void Add(List<PlannedQuery> rungs, string stage, int tier, NewznabSearchMode mode, string? text, IReadOnlyList<KeyValuePair<string, string>> parameters, string provenance)
        {
            var normalized = text is null ? null : Normalize(text);
            if (normalized is { Length: 0 })
            {
                return;
            }

            rungs.Add(new PlannedQuery(stage, tier, mode, normalized, parameters, provenance));
        }

        private static List<string> Titles(SearchIntent intent) =>
            [.. new[] { intent.Title }.Concat(intent.Aliases ?? []).Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10)];

        // The title before a subtitle separator (":", ";" or " - "), the form releases of books are usually named by.
        public static string MainTitle(string title)
        {
            var cut = title.IndexOfAny([':', ';']);
            var dash = title.IndexOf(" - ", StringComparison.Ordinal);
            var end = new[] { cut, dash }.Where(index => index > 0).DefaultIfEmpty(-1).Min();
            return Normalize(end > 0 ? title[..end] : title);
        }

        private static string? NumericId(string id)
        {
            var digits = new string(id.Where(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : null;
        }

        private static string Normalize(string value) =>
            string.Join(' ', (value ?? string.Empty).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
