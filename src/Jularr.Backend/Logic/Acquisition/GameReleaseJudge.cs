using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The game side of release identity: title, then the platform the release is for.
    internal static partial class GameReleaseJudge
    {
        // Format of a release that names none.
        public const string UnknownFormat = "GAME";

        // IGDB platform: the words releases use for it, its Newznab category and whether it is a retro system.
        private static readonly Dictionary<int, (string[] Words, int Category, bool Retro)> Platforms = new()
        {
            [6] = (["pc", "win", "windows", "gog"], 4050, false), [14] = (["mac", "macos", "osx"], 4030, false), [3] = (["linux"], 4000, false),
            [130] = (["nsw", "switch"], 1000, false), [167] = (["ps5"], 1000, false), [48] = (["ps4"], 1180, false), [9] = (["ps3"], 1080, false),
            [169] = (["xsx", "xbsx", "xboxseries", "xboxseriesx"], 1000, false), [49] = (["xboxone", "xone"], 1140, false), [12] = (["x360", "xbox360"], 1050, false),
            [41] = (["wiiu"], 1130, false), [5] = (["wii"], 1030, false), [37] = (["3ds"], 1110, false), [20] = (["nds"], 1010, false), [38] = (["psp"], 1020, false),
            [46] = (["psv", "vita"], 1120, false), [8] = (["ps2"], 1000, true), [7] = (["ps1", "psx"], 1000, true), [11] = (["xbox"], 1040, true), [21] = (["ngc", "gamecube"], 1000, true),
            [18] = (["nes"], 1000, true), [19] = (["snes", "sfc"], 1000, true), [4] = (["n64"], 1000, true), [33] = (["gb"], 1000, true), [22] = (["gbc"], 1000, true),
            [24] = (["gba"], 1000, true), [29] = (["genesis", "megadrive"], 1000, true), [64] = (["sms"], 1000, true), [32] = (["saturn"], 1000, true), [23] = (["dreamcast"], 1000, true),
            [86] = (["pce", "tg16"], 1000, true)
        };

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "proper", "repack", "multi", "multi2", "multi3", "multi5", "multi10", "iso", "nsp", "xci", "rom", "gog", "eur", "usa", "jpn", "pal", "ntsc"
        };

        // Words that point to a part of the game rather than the game.
        private static readonly HashSet<string> Partial = new(StringComparer.OrdinalIgnoreCase) { "update", "dlc", "patch", "crackfix", "trainer", "soundtrack", "ost" };

        [GeneratedRegex(@"^(\d{1,2}|ii|iii|iv|v|vi|vii|viii|ix|x)$")]
        private static partial Regex Sequel();

        [GeneratedRegex(@"^v\d+$")]
        private static partial Regex Version();

        public static int Category(int? igdbPlatform) => igdbPlatform is { } id && Platforms.TryGetValue(id, out var platform) ? platform.Category : 1000;

        public static bool IsRetro(int? igdbPlatform) => igdbPlatform is { } id && Platforms.TryGetValue(id, out var platform) && platform.Retro;

        public static ReleaseJudgement<GameIdentityMatch> Judge(IReadOnlyList<string> titles, int? igdbPlatform, string? abbreviation, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<GameIdentityMatch>(GameIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            // The title's words must be there; a further number names another part of the series.
            var parsed = Parse(offer.Title, igdbPlatform);
            var tokens = ReleaseWords.Tokens(offer.Title).Select(token => token.ToLowerInvariant()).ToList();
            var words = ReleaseWords.Words(offer.Title, Noise);
            var title = titles.Select(value => ReleaseWords.Words(value, Noise)).Where(value => value.Count > 0 && value.All(words.Contains)).MinBy(value => value.Count);
            if (title is null)
            {
                return Result(GameIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            if (words.FirstOrDefault(word => Partial.Contains(word)) is { } partial)
            {
                return Result(GameIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("PartOfGame", partial));
            }

            // The title's own words name neither a platform nor a sequel; platform names and version numbers are no sequel either.
            foreach (var word in title)
            {
                tokens.Remove(word);
            }

            var wanted = Words(igdbPlatform, abbreviation);
            var named = Named(tokens, wanted);
            if (Unversioned(tokens).FirstOrDefault(token => Sequel().IsMatch(token)) is { } sequel)
            {
                return Result(GameIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("OtherEdition", sequel));
            }

            // Every platform the release names must be the wanted one. Unnamed releases are PC releases.
            if (named.Count > 0)
            {
                return named.All(wanted.Contains)
                    ? Result(GameIdentityMatch.Matches, ReleaseIdentityEvidence.Strong("MatchesTitleAndPlatform"))
                    : Result(GameIdentityMatch.WrongPlatform, ReleaseIdentityEvidence.Conflict("WrongPlatform", string.Join(' ', named.Where(word => !wanted.Contains(word)))));
            }

            return igdbPlatform == 6
                ? Result(GameIdentityMatch.Matches, ReleaseIdentityEvidence.Strong("MatchesTitle"))
                : Result(GameIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("PlatformMissing"));

            ReleaseJudgement<GameIdentityMatch> Result(GameIdentityMatch match, ReleaseIdentityEvidence evidence) => new(match, parsed, evidence, SelectionCoverage.Single, null);
        }

        // Game names carry the title, the platform and the release form; video fields stay empty.
        public static ReleaseInfo Parse(string title, int? igdbPlatform)
        {
            var tokens = ReleaseWords.Tokens(title).Select(token => token.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var format = tokens.Contains("gog") ? "GOG" : tokens.Contains("nsp") ? "NSP" : tokens.Contains("xci") ? "XCI" : tokens.Contains("iso") ? "ISO" : IsRetro(igdbPlatform) ? "ROM" : UnknownFormat;
            var name = string.Join(' ', ReleaseWords.Tokens(title).Where(token => !Noise.Contains(token) && !ReleaseWords.YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper"), tokens.Contains("repack"), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format
            };
        }

        private static HashSet<string> Words(int? igdbPlatform, string? abbreviation)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            if (igdbPlatform is { } id && Platforms.TryGetValue(id, out var platform))
            {
                words.UnionWith(platform.Words);
            }

            if (abbreviation is { Length: > 0 })
            {
                words.Add(string.Concat(abbreviation.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant());
            }

            return words;
        }

        // Takes the platform names a release uses out of its tokens; words run together ("xbox one", "wii u") win over their first word.
        private static HashSet<string> Named(List<string> tokens, HashSet<string> wanted)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < tokens.Count; index++)
            {
                for (var length = Math.Min(3, tokens.Count - index); length > 0; length--)
                {
                    var word = string.Concat(tokens.Skip(index).Take(length));
                    if (IsPlatform(word))
                    {
                        named.Add(word);
                        tokens.RemoveRange(index--, length);
                        break;
                    }
                }
            }

            return named;

            bool IsPlatform(string word) => wanted.Contains(word) || Platforms.Values.Any(platform => platform.Words.Contains(word));
        }

        // "v1.0.3" splits into "v1", "0" and "3"; the numbers after a version mark belong to it.
        private static IEnumerable<string> Unversioned(List<string> tokens)
        {
            var version = false;
            foreach (var token in tokens)
            {
                version = Version().IsMatch(token) || version && token.All(char.IsAsciiDigit);
                if (!version)
                {
                    yield return token;
                }
            }
        }
    }
}
