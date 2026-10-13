using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The music side of release identity: artist and album before format.
    internal static class MusicReleaseJudge
    {
        // Format of a release whose name does not say; the scan reads the streams.
        public const string UnknownFormat = "AUDIO";

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "flac", "mp3", "aac", "alac", "m4a", "320", "v0", "web", "cd", "vinyl", "lossless", "24bit", "16bit", "hires", "hi", "res", "album", "proper", "repack"
        };

        // Words that name another edition of the same album.
        private static readonly HashSet<string> Editions = new(StringComparer.OrdinalIgnoreCase)
        {
            "live", "deluxe", "remix", "remixes", "remixed", "acoustic", "instrumental", "karaoke", "tribute", "covers", "demos", "single", "ep"
        };

        public static ReleaseJudgement<MusicIdentityMatch> Judge(IReadOnlyList<string> titles, string? artist, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<MusicIdentityMatch>(MusicIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            // The album and the artist must both appear as their own word runs.
            var parsed = Parse(offer.Title);
            var words = Sequence(offer.Title);
            var album = titles.Select(Sequence).Where(title => title.Length > 0 && Contains(words, title)).MinBy(title => title.Length);
            if (album is null)
            {
                return Result(MusicIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            var artists = Sequence(artist ?? "");
            if (artists.Length == 0)
            {
                return Result(MusicIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("ArtistMissing"));
            }

            if (!Contains(words, artists))
            {
                return Result(MusicIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongArtist", artist));
            }

            var edition = words.Where(word => Editions.Contains(word) && !album.Contains(word)).Distinct().ToArray();
            return edition.Length > 0
                ? Result(MusicIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("OtherEdition", string.Join(' ', edition)))
                : Result(MusicIdentityMatch.Matches, offer.Stages.Contains("id") ? ReleaseIdentityEvidence.Exact("MatchesArtistAndAlbum") : ReleaseIdentityEvidence.Strong("MatchesArtistAndAlbum"));

            ReleaseJudgement<MusicIdentityMatch> Result(MusicIdentityMatch match, ReleaseIdentityEvidence evidence) => new(match, parsed, evidence, SelectionCoverage.Single, null);
        }

        private static string[] Sequence(string value) => [.. ReleaseWords.Tokens(value).Select(word => word.ToLowerInvariant()).Where(word => !Noise.Contains(word))];

        // The run of words appears in order without gaps, so another artist sharing a word does not match.
        private static bool Contains(string[] words, string[] run) =>
            Enumerable.Range(0, Math.Max(0, words.Length - run.Length + 1)).Any(start => run.Select((word, index) => words[start + index] == word).All(match => match));

                // Music names carry the format and sometimes its bit depth or bitrate; video fields stay empty.
        public static ReleaseInfo Parse(string title)
        {
            var tokens = ReleaseWords.Tokens(title).Select(token => token.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var format = tokens.Contains("flac") ? tokens.Contains("24bit") || tokens.Contains("hires") || title.Contains("24-bit", StringComparison.OrdinalIgnoreCase) ? "FLAC-24" : "FLAC"
                : tokens.Contains("alac") ? "ALAC"
                : tokens.Contains("mp3") ? tokens.Contains("320") ? "MP3-320" : tokens.Contains("v0") ? "MP3-V0" : "MP3"
                : tokens.Contains("aac") || tokens.Contains("m4a") ? "AAC"
                : UnknownFormat;
            var name = string.Join(' ', ReleaseWords.Tokens(title).Where(token => !Noise.Contains(token) && !ReleaseWords.YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper"), tokens.Contains("repack"), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format
            };
        }
    }
}
