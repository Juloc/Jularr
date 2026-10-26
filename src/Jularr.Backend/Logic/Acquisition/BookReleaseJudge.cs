using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The book side of release identity: title and author before format and quality.
    internal static class BookReleaseJudge
    {
        // Format of an ebook whose name does not say; the import checks the content.
        public const string UnknownFormat = "EBOOK";

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "der", "die", "das", "und", "le", "la", "les", "retail", "ebook", "e", "book", "epub", "pdf", "mobi", "azw3", "azw", "kepub", "repack",
            "proper", "scan", "ocr", "german", "deutsch", "english", "french", "francais", "spanish", "italian", "japanese", "novel", "light", "vol", "volume", "edition", "unabridged"
        };

        private static readonly Dictionary<string, string> Formats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["epub"] = "EPUB", ["kepub"] = "EPUB", ["azw3"] = "AZW3", ["azw"] = "AZW3", ["mobi"] = "MOBI", ["pdf"] = "PDF"
        };

        private static readonly string[] DerivativeMarkers =
        [
            " summary ", " summaries ", " summarized ", " summarised ", " zusammenfassung ", " kurzfassung ",
            " study guide ", " expert guide ", " workbook ", " abridged ", " condensed "
        ];

        public static ReleaseJudgement<BookIdentityMatch> Judge(string title, string? author, ReleaseOffer offer) => Judge([title], author, offer);

        // The work's title or the title of an edition in a requested language.
        public static ReleaseJudgement<BookIdentityMatch> Judge(IReadOnlyList<string> titles, string? author, ReleaseOffer offer, string? originalLanguage = null)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            var parsed = Parse(offer.Title);
            var words = ReleaseWords.Words(offer.Title, Noise);
            var wanted = titles.Select(title => ReleaseWords.Words(title, Noise)).FirstOrDefault(candidate => candidate.Count > 0 && candidate.All(words.Contains))
                ?? ReleaseWords.Words(titles[0], Noise);
            var languages = ReleaseWords.ReadLanguages(offer.Title, "");
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

            // A named author confirms the work; a missing one leaves it to a person.
            var surnames = Authors(author).Select(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant()).OfType<string>().Where(name => name.Length > 1).ToArray();
            if (surnames.Length > 0)
            {
                return surnames.Any(words.Contains) || viaId
                    ? Result(BookIdentityMatch.Matches, ReleaseIdentityEvidence.Exact("MatchesTitleAndAuthor"))
                    : Result(BookIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AuthorMissing", author));
            }

            // Without an author, more words than the title mean another book of the same name.
            var extra = words.Where(word => !wanted.Contains(word) && !ReleaseWords.YearToken().IsMatch(word)).ToArray();
            return extra.Length > 0 && !viaId
                ? Result(BookIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", string.Join(' ', extra)))
                : Result(BookIdentityMatch.Matches, ReleaseIdentityEvidence.Strong("Matches"));

            ReleaseJudgement<BookIdentityMatch> Result(BookIdentityMatch match, ReleaseIdentityEvidence evidence) =>
                new(match, parsed, evidence, SelectionCoverage.Single, null) { Languages = languages };
        }

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
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper", StringComparer.OrdinalIgnoreCase),
                tokens.Contains("repack", StringComparer.OrdinalIgnoreCase), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format
            };
        }
    }
}
