using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The book side of release identity: title and author before format and quality, then what the name states (kind, volumes, editions) and its size per volume.
    internal static partial class BookReleaseJudge
    {
        // Format of an ebook whose name does not say; the import checks the content.
        public const string UnknownFormat = "EBOOK";

        // The size rows of books (quality sizes of the book media type, in megabytes per volume).
        public const string EbookSize = "Ebook";
        public const string IllustratedSize = "Illustrated";
        public const string MangaSize = "Manga";

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "der", "die", "das", "und", "le", "la", "les", "retail", "ebook", "e", "book", "epub", "pdf", "mobi", "azw3", "azw", "kepub", "repack",
            "proper", "scan", "ocr", "german", "deutsch", "english", "french", "francais", "spanish", "italian", "japanese", "novel", "light", "vol", "volume", "edition", "unabridged"
        };

        private static readonly Dictionary<string, string> Formats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["epub"] = "EPUB", ["kepub"] = "EPUB", ["azw3"] = "AZW3", ["azw"] = "AZW3", ["mobi"] = "MOBI", ["pdf"] = "PDF"
        };

        // Comic and audio files a book search can also find.
        private static readonly Dictionary<string, string> OtherFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cbz"] = "CBZ", ["cbr"] = "CBR", ["cb7"] = "CB7", ["m4b"] = "M4B", ["m4a"] = "M4A", ["mp3"] = "MP3", ["flac"] = "FLAC"
        };

        // The rank of the ebook formats as points: EPUB before AZW3 and MOBI before PDF.
        private static readonly Dictionary<string, int> FormatPoints = new(StringComparer.Ordinal) { ["EPUB"] = 40, ["AZW3"] = 30, ["MOBI"] = 30, ["PDF"] = 10 };

        // A language code alone in brackets, as "(EN)".
        private static readonly Dictionary<string, string> LanguageTags = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "EN", ["eng"] = "EN", ["de"] = "DE", ["ger"] = "DE", ["fr"] = "FR", ["es"] = "ES", ["it"] = "IT", ["ja"] = "JA", ["jp"] = "JA", ["pt"] = "PT", ["ru"] = "RU",
            ["zh"] = "ZH", ["ko"] = "KO", ["nl"] = "NL", ["pl"] = "PL"
        };

        // Words that describe a release rather than name its book; a tag of only such words is no publisher or group.
        private static readonly HashSet<string> Describing = new(StringComparer.OrdinalIgnoreCase)
        {
            "digital", "compilation", "complete", "completed", "web", "novel", "wn", "light", "ln", "illustrated", "retail", "audiobook", "audio", "book", "hörbuch", "horbuch",
            "unabridged", "narrated", "manga", "comic", "comics", "webtoon", "magazine", "ebook", "scan", "ocr", "edition", "repack", "proper", "english", "german", "deutsch",
            "french", "francais", "spanish", "italian", "japanese", "vol", "vols", "volume", "volumes"
        };

        // Publishers and imprints of books, light novels and manga, by their first words: a tag that starts with one names the publisher, never the group.
        private static readonly string[][] Publishers = [.. new[]
        {
            "seven seas", "yen press", "yen on", "kodansha", "square enix", "j-novel club", "jnc", "viz", "vertical", "dark horse", "tokyopop", "one peace", "cross infinite world",
            "airship", "ghost ship", "denpa", "kadokawa", "shueisha", "shogakukan", "hakusensha", "media factory", "del rey", "udon", "sol press", "hanashi media", "tentai books",
            "j-pop", "carlsen", "egmont", "tokyopop", "altraverse", "crunchyroll", "penguin", "harpercollins", "tor books", "orbit", "gollancz", "simon & schuster", "macmillan"
        }.Distinct().Select(name => ReleaseWords.Tokens(name).Select(word => word.ToLowerInvariant()).ToArray())];

        // Edition words left out of the title a name states.
        private static readonly HashSet<string> EditionWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "digital", "compilation", "complete", "completed", "illustrated", "retail", "audiobook", "unabridged"
        };

        private static readonly string[] DerivativeMarkers =
        [
            " summary ", " summaries ", " summarized ", " summarised ", " zusammenfassung ", " kurzfassung ",
            " study guide ", " expert guide ", " workbook ", " abridged ", " condensed "
        ];

        // A tag in brackets; tags do not nest.
        [GeneratedRegex(@"[\[({](?<tag>[^\[\](){}]*)[\])}]")]
        private static partial Regex Tag();

        // A volume or a range ("v01-23", "Vol 01-24", "Volume 3"); a range end is never a year.
        [GeneratedRegex(@"(?<![\p{L}\d])(?<marker>v|vols?\.?|volumes?)\s?(?<start>\d{1,4})(?:\s?-\s?(?:v|vols?\.?|volumes?)?\s?(?!(?:19|20)\d{2}(?!\d))(?<end>\d{1,4}))?(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Volumes();

        [GeneratedRegex(@"^(?<year>(?:1[5-9]|20)\d{2})(?:\s?-\s?(?:1[5-9]|20)\d{2})?$")]
        private static partial Regex YearTag();

        // A dotted scene name ends in its group: "Title.2019.RETAIL.EPUB-GROUP".
        [GeneratedRegex(@"^(?<name>[^\s]+\.[^\s]+)-(?<group>[A-Za-z0-9]+)$")]
        private static partial Regex SceneGroup();

        [GeneratedRegex(@"(?<![\p{L}])(?:web[\s._-]?novel|wn)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex WebNovel();

        [GeneratedRegex(@"(?<![\p{L}])(?:light[\s._-]?novel|ln)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex LightNovel();

        [GeneratedRegex(@"(?<![\p{L}])(?:audio[\s._-]?book|h[öo]rbuch)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Audiobook();

        public static ReleaseJudgement<BookIdentityMatch> Judge(string title, string? author, ReleaseOffer offer) => Judge([title], author, offer);

        // The work's title or the title of an edition in a requested language.
        public static ReleaseJudgement<BookIdentityMatch> Judge(IReadOnlyList<string> titles, string? author, ReleaseOffer offer, string? originalLanguage = null, BookSearch? search = null)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            var parsed = Parse(offer.Title);
            var name = parsed.Book!;
            var sizeMark = SizeOf(name, offer.SizeBytes, search);
            var words = ReleaseWords.Words(offer.Title, Noise);
            var wanted = titles.Select(title => ReleaseWords.Words(title, Noise)).FirstOrDefault(candidate => candidate.Count > 0 && candidate.All(words.Contains))
                ?? ReleaseWords.Words(titles[0], Noise);
            var viaId = offer.Stages.Contains("id");
            if (wanted.Count == 0 || !wanted.All(words.Contains))
            {
                return viaId
                    ? Result(BookIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle))
                    : Result(BookIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            // A guide or summary can repeat both the original title and author. Neither is proof of the original.
            if (IsUnrequestedDerivative(titles, author, offer.Title, words))
            {
                return Result(BookIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            // A named author confirms the work. Without it the title alone is strong unless the name adds words of its own (another book of the series, a spin-off);
            // a web novel is another text of the same story.
            var surnames = Authors(author).Select(person => person.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant()).OfType<string>().Where(person => person.Length > 1).ToArray();
            var authorWords = ReleaseWords.Tokens(author ?? "").Select(word => word.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var authorNamed = surnames.Any(words.Contains);
            var extra = ReleaseWords.Tokens(name.Title).Select(word => word.ToLowerInvariant())
                .Where(word => !Noise.Contains(word) && !wanted.Contains(word) && !authorWords.Contains(word)).Distinct(StringComparer.Ordinal).ToArray();
            var identity = name.Editions.Contains("WebNovel") && !titles.Any(title => WebNovel().IsMatch(title)) ? ReleaseIdentityEvidence.Ambiguous("WebNovel")
                : authorNamed || viaId && surnames.Length > 0 ? ReleaseIdentityEvidence.Exact("MatchesTitleAndAuthor")
                : viaId ? ReleaseIdentityEvidence.Strong("Matches")
                : extra.Length > 0 ? ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", string.Join(' ', extra))
                : ReleaseIdentityEvidence.Strong("MatchesTitle");

            // Points of the request's rules: the title, a named author, the format's rank and an illustrated edition.
            var points = new List<ScorePoint> { new("TitleMatches", null, 20) };
            if (authorNamed) points.Add(new ScorePoint("AuthorNamed", null, 10));
            points.Add(name.Format is { } format ? new ScorePoint("Format", format, FormatPoints.GetValueOrDefault(format)) : new ScorePoint("FormatNotNamed", null, 0));
            if (name.Editions.Contains("Illustrated")) points.Add(new ScorePoint("Illustrated", null, 15));

            // An e-book request takes no other kind. A size outside the limits rejects an automatic choice and only marks it for a person.
            var rejection = name.Kind switch
            {
                BookReleaseKind.Audiobook => new SelectionReason(SelectionReasonKind.Profile, "AudiobookNotWanted"),
                BookReleaseKind.MangaComic => new SelectionReason(SelectionReasonKind.Profile, "MangaNotWanted"),
                BookReleaseKind.Magazine => new SelectionReason(SelectionReasonKind.Profile, "MagazineNotWanted"),
                _ => null
            };
            var size = sizeMark.Reason is { } outside && rejection is null ? outside : null;
            var automatic = search?.Automatic ?? true;

            // At equal quality a single volume comes before a pack: every further volume costs coverage.
            var coverage = name.VolumeCount is { } count and > 1 ? new SelectionCoverage(1, 1, count - 1) : SelectionCoverage.Single;
            return new ReleaseJudgement<BookIdentityMatch>(rejection is not null ? BookIdentityMatch.WrongKind
                : identity.Confidence == IdentityConfidence.Ambiguous ? BookIdentityMatch.AmbiguousTitle : BookIdentityMatch.Matches, parsed, identity, coverage, null)
            {
                Languages = name.Languages,
                Points = points,
                Rejection = rejection ?? (automatic ? size : null),
                Flag = automatic ? null : size,
                SizeMark = sizeMark.Mark
            };

            ReleaseJudgement<BookIdentityMatch> Result(BookIdentityMatch match, ReleaseIdentityEvidence evidence) =>
                new(match, parsed, evidence, SelectionCoverage.Single, null) { Languages = name.Languages, SizeMark = sizeMark.Mark };
        }

        // The size per volume against the limits of the release's kind; an illustrated edition has its own.
        private static (ReleaseSizeMark Mark, SelectionReason? Reason) SizeOf(BookReleaseName name, long? sizeBytes, BookSearch? search)
        {
            var key = name.Kind switch
            {
                BookReleaseKind.Ebook => name.Editions.Contains("Illustrated") ? IllustratedSize : EbookSize,
                BookReleaseKind.MangaComic => MangaSize,
                _ => null
            };
            if (key is null || search is null || !search.Sizes.TryGetValue(key, out var limit) || limit.Minimum <= 0 && limit.Maximum is null) return (ReleaseSizeMark.NoLimit, null);
            if (sizeBytes is not { } bytes) return (ReleaseSizeMark.Unknown, null);

            // An unnumbered release of a series holds an unknown number of volumes; it only surely fits when it fits as a whole.
            var megabytes = bytes / 1_048_576m;
            if ((name.VolumeCount ?? (search.IsSeries ? null : 1)) is not { } volumes)
            {
                return limit.Minimum <= 0 && (limit.Maximum is not { } whole || megabytes <= whole) ? (ReleaseSizeMark.Fits, null) : (ReleaseSizeMark.Unknown, null);
            }

            var perVolume = megabytes / volumes;
            if (perVolume < limit.Minimum)
            {
                return (ReleaseSizeMark.BelowMinimum, new SelectionReason(SelectionReasonKind.Quality, "SizeBelowVolumeMinimum", Megabytes(perVolume) + " < " + Megabytes(limit.Minimum)));
            }

            return limit.Maximum is { } maximum && perVolume > maximum
                ? (ReleaseSizeMark.AboveMaximum, new SelectionReason(SelectionReasonKind.Quality, "SizeAboveVolumeMaximum", Megabytes(perVolume) + " > " + Megabytes(maximum)))
                : (ReleaseSizeMark.Fits, null);
        }

        private static string Megabytes(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

        private static bool IsUnrequestedDerivative(IReadOnlyList<string> titles, string? author, string candidate, IReadOnlySet<string> words)
        {
            var authorWords = ReleaseWords.Tokens(author ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = IdentityName(ReleaseWords.Tokens(candidate).SkipWhile(authorWords.Contains));
            var markers = DerivativeMarkers.Where(marker => name.Contains(marker, StringComparison.Ordinal)).ToArray();
            var guideAt = name.IndexOf(" guide ", StringComparison.Ordinal);
            if (markers.Length == 0 && guideAt < 0) return false;

            foreach (var title in titles)
            {
                var requested = ReleaseWords.Words(title, Noise);
                if (requested.Count == 0 || !requested.All(words.Contains)) continue;
                var expected = IdentityName(ReleaseWords.Tokens(title));
                if (markers.Any(marker => !expected.Contains(marker, StringComparison.Ordinal))) continue;

                // A guide *to the requested title* differs from a normal subtitle after that title.
                if (guideAt >= 0 && name.IndexOf(expected, StringComparison.Ordinal) > guideAt) continue;
                return false;
            }

            return true;
        }

        private static string IdentityName(IEnumerable<string> tokens) =>
            " " + string.Join(' ', tokens.Where(token => !Noise.Contains(token)).Select(token => token.ToLowerInvariant())) + " ";

        // Authors listed with commas, ampersands or semicolons.
        public static string[] Authors(string? author) => (author ?? "").Split([',', '&', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Book names carry the format as a word; scene video fields stay empty.
        public static ReleaseInfo Parse(string title)
        {
            var tokens = ReleaseWords.Tokens(title);
            var format = tokens.Select(token => Formats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault() ?? UnknownFormat;
            var name = string.Join(' ', tokens.Where(token => !Noise.Contains(token) && !ReleaseWords.YearToken().IsMatch(token)));
            var book = ReadName(title);
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, book.Group, 1, tokens.Contains("proper", StringComparer.OrdinalIgnoreCase),
                tokens.Contains("repack", StringComparer.OrdinalIgnoreCase), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format,
                Book = book
            };
        }

        // Whether a tag names a known publisher or imprint ("Seven Seas Entertainment", "Seven Seas Siren").
        private static bool IsPublisher(string tag)
        {
            var words = ReleaseWords.Tokens(tag).Select(word => word.ToLowerInvariant()).ToArray();
            return Publishers.Any(publisher => publisher.Length <= words.Length && publisher.SequenceEqual(words.Take(publisher.Length)));
        }

        // Everything a book release name states, without the request: a pure function of the name.
        public static BookReleaseName ReadName(string title)
        {
            // Bracketed tags describe the release; the rest is its title with volume and edition words.
            var tags = new List<string>();
            var rest = Tag().Replace(title, match =>
            {
                tags.Add(match.Groups["tag"].Value.Trim());
                return " ";
            });
            string? sceneGroup = null;
            if (SceneGroup().Match(rest.Trim()) is { Success: true } scene)
            {
                sceneGroup = scene.Groups["group"].Value;
                rest = scene.Groups["name"].Value;
            }

            var volumes = Volumes().Match(rest);
            if (volumes.Success) rest = rest.Remove(volumes.Index, volumes.Length);
            var tokens = ReleaseWords.Tokens(title).Select(token => token.ToLowerInvariant()).ToArray();
            bool Has(params string[] words) => words.Any(tokens.Contains);

            // The format: an ebook format first, else a comic or audio file.
            var ebookFormat = tokens.Select(token => Formats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault();
            var otherFormat = tokens.Select(token => OtherFormats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault();

            // Languages, years, and the tags that name no publisher or group.
            var languages = ReleaseWords.ReadLanguages(title, "").ToList();
            int? year = null;
            var named = new List<string>();
            foreach (var tag in tags.Where(tag => tag.Length > 0))
            {
                if (LanguageTags.TryGetValue(tag, out var language))
                {
                    if (!languages.Contains(language)) languages.Add(language);
                    continue;
                }

                if (YearTag().Match(tag) is { Success: true } years)
                {
                    year ??= int.Parse(years.Groups["year"].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                var words = ReleaseWords.Tokens(Volumes().Replace(tag, " "));
                if (words.All(word => Describing.Contains(word) || Formats.ContainsKey(word) || OtherFormats.ContainsKey(word) || word.All(char.IsAsciiDigit))) continue;
                named.Add(tag);
            }

            var titleWords = ReleaseWords.Tokens(LightNovel().Replace(WebNovel().Replace(Audiobook().Replace(rest, " "), " "), " "))
                .Where(token => !EditionWords.Contains(token) && !Formats.ContainsKey(token) && !OtherFormats.ContainsKey(token)).ToArray();
            year ??= titleWords.Where(token => ReleaseWords.YearToken().IsMatch(token)).Select(token => (int?)int.Parse(token, CultureInfo.InvariantCulture)).FirstOrDefault();

            // Edition words in a fixed order.
            var digitalCompilation = tags.Any(tag => ReleaseWords.Tokens(tag) is var words && words.Contains("digital", StringComparer.OrdinalIgnoreCase)
                && words.Contains("compilation", StringComparer.OrdinalIgnoreCase));
            var webNovel = WebNovel().IsMatch(title);
            var lightNovel = LightNovel().IsMatch(title);
            var editions = new List<string>();
            if (Has("digital")) editions.Add("Digital");
            if (webNovel) editions.Add("WebNovel");
            if (Has("compilation")) editions.Add("Compilation");
            if (Has("complete", "completed")) editions.Add("Complete");
            if (Has("illustrated")) editions.Add("Illustrated");

            // The kind: an audiobook says so or names an audio file, a magazine says so, and a comic names its file, says so or follows the comic naming
            // "Title v01 (2015) (Digital) (Group)"; everything else is an e-book.
            var comicNaming = volumes.Success && volumes.Groups["marker"].Value.Equals("v", StringComparison.OrdinalIgnoreCase) && year is not null && Has("digital")
                && ebookFormat is null && !lightNovel && !webNovel;
            var kind = Audiobook().IsMatch(title) || otherFormat is "M4B" or "M4A" or "MP3" or "FLAC" || ebookFormat is null && Has("narrated", "unabridged") ? BookReleaseKind.Audiobook
                : Has("magazine", "magazines", "magazin", "zeitschrift") ? BookReleaseKind.Magazine
                : otherFormat is "CBZ" or "CBR" or "CB7" || Has("manga", "comic", "comics", "webtoon") || digitalCompilation || comicNaming ? BookReleaseKind.MangaComic
                : BookReleaseKind.Ebook;

            // A known publisher is the publisher wherever it stands; of the other named tags the last is the group (as Nyaa and comic names put it), the one before it the publisher.
            var groups = named.Where(tag => !IsPublisher(tag)).ToList();
            var group = sceneGroup ?? groups.LastOrDefault();
            var publisher = named.LastOrDefault(IsPublisher) ?? (sceneGroup is null && groups.Count > 1 ? groups[^2] : null);
            int? start = volumes.Success ? int.Parse(volumes.Groups["start"].Value, CultureInfo.InvariantCulture) : null;
            int? end = volumes.Success && volumes.Groups["end"].Success ? int.Parse(volumes.Groups["end"].Value, CultureInfo.InvariantCulture) : null;
            return new BookReleaseName(string.Join(' ', titleWords.Where(token => !ReleaseWords.YearToken().IsMatch(token))), kind, ebookFormat ?? otherFormat,
                start, end is { } last && last > start ? last : null, editions, publisher, group, year, languages);
        }
    }
}
