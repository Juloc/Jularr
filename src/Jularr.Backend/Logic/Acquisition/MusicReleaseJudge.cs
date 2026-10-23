using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The music side of release identity: artist and album before format.
    internal static partial class MusicReleaseJudge
    {
        // Format of a release whose name does not say; the scan reads the streams.
        public const string UnknownFormat = "AUDIO";

        // Words that name another edition or another part of the same album.
        private static readonly HashSet<string> Editions = new(StringComparer.Ordinal)
        {
            "live", "deluxe", "remix", "remixes", "remixed", "acoustic", "instrumental", "karaoke", "tribute", "covers", "demos", "single", "ep", "vol", "volume", "part", "pt",
            "ii", "iii", "iv", "vi", "vii", "viii", "ix"
        };

        private static readonly HashSet<string> Compilations = new(StringComparer.Ordinal) { "va", "various" };

        [GeneratedRegex(@"24[-\s]?bit|\b24[-/\s](44|48|88|96|176|192)\b|hi[-\s]?res", RegexOptions.IgnoreCase)]
        private static partial Regex HighResolution();

        [GeneratedRegex(@"\b320\s?(k|kbps)?\b", RegexOptions.IgnoreCase)]
        private static partial Regex Bitrate320();

        [GeneratedRegex(@"\bv0\b", RegexOptions.IgnoreCase)]
        private static partial Regex VariableV0();

        public static ReleaseJudgement<MusicIdentityMatch> Judge(IReadOnlyList<string> titles, string? artist, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<MusicIdentityMatch>(MusicIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            // Artist and album are separate word runs, so a self-titled album needs both.
            var parsed = Parse(offer.Title);
            var words = Sequence(offer.Title);
            var artists = Sequence(artist ?? "");
            if (artists.Length == 0)
            {
                return Result(MusicIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("ArtistMissing"));
            }

            var compilation = artists.SequenceEqual(["various", "artists"]);
            var artistRuns = compilation ? Enumerable.Range(0, words.Length).Where(index => Compilations.Contains(words[index])).Select(index => (Start: index, Length: 1)).ToArray() : Runs(words, artists);
            if (artistRuns.Length == 0)
            {
                return Result(MusicIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongArtist", artist));
            }

            var match = titles.Select(Sequence).Where(album => album.Length > 0)
                .SelectMany(album => Runs(words, album).SelectMany(albumRun => artistRuns.Where(artistRun => albumRun.Start >= artistRun.Start + artistRun.Length || artistRun.Start >= albumRun.Start + albumRun.Length)
                    .Select(artistRun => (Album: albumRun, Artist: artistRun))))
                .FirstOrDefault();
            if (match == default)
            {
                return Result(MusicIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            // Edition and sequel words outside the matched names point to another release.
            var edition = words.Where((word, index) => Editions.Contains(word) && !Inside(index, match.Album) && !Inside(index, match.Artist)).Distinct().ToArray();
            return edition.Length > 0
                ? Result(MusicIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("OtherEdition", string.Join(' ', edition)))
                : Result(MusicIdentityMatch.Matches, offer.Stages.Contains("id") ? ReleaseIdentityEvidence.Exact("MatchesArtistAndAlbum") : ReleaseIdentityEvidence.Strong("MatchesArtistAndAlbum"));

            ReleaseJudgement<MusicIdentityMatch> Result(MusicIdentityMatch result, ReleaseIdentityEvidence evidence) => new(result, parsed, evidence, SelectionCoverage.Single, null);
        }

        // Music names carry the format and sometimes its bit depth or bitrate; video fields stay empty.
        public static ReleaseInfo Parse(string title)
        {
            var tokens = ReleaseWords.Tokens(title).Select(token => token.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var format = tokens.Contains("flac") ? HighResolution().IsMatch(title) ? "FLAC-24" : "FLAC"
                : tokens.Contains("alac") ? "ALAC"
                : tokens.Contains("mp3") ? Bitrate320().IsMatch(title) ? "MP3-320" : VariableV0().IsMatch(title) ? "MP3-V0" : "MP3"
                : tokens.Contains("aac") || tokens.Contains("m4a") ? "AAC"
                : UnknownFormat;
            var name = string.Join(' ', ReleaseWords.Tokens(title).Where(token => !ReleaseWords.YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper"), tokens.Contains("repack"), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format
            };
        }

        // Lower case without accents or apostrophes, so "Don't" meets "Dont" and "Beyoncé" meets "Beyonce".
        private static string[] Sequence(string value)
        {
            var folded = new StringBuilder();
            foreach (var character in value.Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal).Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    folded.Append(char.ToLowerInvariant(character));
                }
            }

            return ReleaseWords.Tokens(folded.ToString());
        }

        // Every place the run appears without gaps; "AC/DC" also meets "ACDC".
        private static (int Start, int Length)[] Runs(string[] words, string[] run)
        {
            var exact = Enumerable.Range(0, Math.Max(0, words.Length - run.Length + 1)).Where(start => run.Select((word, index) => words[start + index] == word).All(same => same))
                .Select(start => (start, run.Length)).ToArray();
            if (exact.Length > 0 || run.Length < 2)
            {
                return exact;
            }

            var joined = string.Concat(run);
            return [.. Enumerable.Range(0, words.Length).Where(index => words[index] == joined).Select(index => (index, 1))];
        }

        private static bool Inside(int index, (int Start, int Length) run) => index >= run.Start && index < run.Start + run.Length;
    }
}
