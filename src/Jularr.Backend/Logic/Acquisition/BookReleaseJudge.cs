using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The book side of release identity: title and author before format and quality.
    internal static partial class BookReleaseJudge
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

        private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["german"] = "DE", ["deutsch"] = "DE", ["french"] = "FR", ["francais"] = "FR", ["spanish"] = "ES", ["italian"] = "IT", ["japanese"] = "JA"
        };

        [GeneratedRegex(@"^(?:1[5-9]|20)\d{2}$")]
        private static partial Regex YearToken();

        public static ReleaseJudgement<BookIdentityMatch> Judge(string title, string? author, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            var parsed = Parse(offer.Title);
            var words = Words(offer.Title);
            var wanted = Words(title);
            var languages = words.Select(word => Languages.GetValueOrDefault(word)).OfType<string>().Distinct().DefaultIfEmpty("EN").ToArray();
            var viaId = offer.Stages.Contains("id");
            if (wanted.Count == 0 || !wanted.All(words.Contains))
            {
                var evidence = viaId ? ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle) : ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle);
                return new ReleaseJudgement<BookIdentityMatch>(viaId ? BookIdentityMatch.AmbiguousTitle : BookIdentityMatch.WrongTitle, parsed, evidence, SelectionCoverage.Single, null)
                {
                    Languages = languages
                };
            }

            // A named author confirms the work; a missing one leaves it to a person.
            var surnames = (author ?? "").Split([',', '&', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant())
                .OfType<string>()
                .Where(name => name.Length > 1)
                .ToArray();
            if (surnames.Length > 0)
            {
                return surnames.Any(words.Contains) || viaId
                    ? new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.Matches, parsed, ReleaseIdentityEvidence.Exact("MatchesTitleAndAuthor"), SelectionCoverage.Single, null) { Languages = languages }
                    : new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.AmbiguousTitle, parsed, ReleaseIdentityEvidence.Ambiguous("AuthorMissing", author), SelectionCoverage.Single, null)
                    {
                        Languages = languages
                    };
            }

            // Without an author, more words than the title mean another book of the same name.
            var extra = words.Where(word => !wanted.Contains(word) && !YearToken().IsMatch(word)).ToArray();
            return extra.Length > 0 && !viaId
                ? new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.AmbiguousTitle, parsed, ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", string.Join(' ', extra)), SelectionCoverage.Single, null)
                {
                    Languages = languages
                }
                : new ReleaseJudgement<BookIdentityMatch>(BookIdentityMatch.Matches, parsed, ReleaseIdentityEvidence.Strong("Matches"), SelectionCoverage.Single, null) { Languages = languages };
        }

        // Book names carry the format as a word; scene video fields stay empty.
        public static ReleaseInfo Parse(string title)
        {
            var tokens = Tokens(title);
            var format = tokens.Select(token => Formats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault() ?? UnknownFormat;
            var name = string.Join(' ', tokens.Where(token => !Noise.Contains(token) && !YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper", StringComparer.OrdinalIgnoreCase),
                tokens.Contains("repack", StringComparer.OrdinalIgnoreCase), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format
            };
        }

        private static HashSet<string> Words(string value) =>
            Tokens(value).Select(word => word.ToLowerInvariant()).Where(word => !Noise.Contains(word)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string[] Tokens(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '{', '}', '\'', '"', '!', '?', '&', '/', '#', '+'], StringSplitOptions.RemoveEmptyEntries);
    }
}
