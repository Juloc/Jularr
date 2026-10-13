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

        public static ReleaseJudgement<BookIdentityMatch> Judge(string title, string? author, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            var parsed = Parse(offer.Title);
            var words = ReleaseWords.Words(offer.Title, Noise);
            var wanted = ReleaseWords.Words(title, Noise);
            var languages = ReleaseWords.ReadLanguages(offer.Title);
            var viaId = offer.Stages.Contains("id");
            if (wanted.Count == 0 || !wanted.All(words.Contains))
            {
                return viaId
                    ? Result(BookIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle))
                    : Result(BookIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
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
