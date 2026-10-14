using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Logic.Library
{
    // What a release name tells about its file.
    internal static partial class ReleaseFacts
    {
        public static ReleaseFact? Of(string? name)
        {
            if (!ReleaseParser.TryParse(name, out var release))
            {
                return null;
            }

            // Jularr's own names carry the quality in brackets.
            var quality = ReleaseQuality.GetKey(release) is var key && !key.Contains("UNKNOWN", StringComparison.Ordinal) ? key
                : QualityTag().Match(release.RawTitle) is { Success: true } tag ? tag.Groups[1].Value : null;

            // Title words such as "Uncut" or a hyphen as in "Spider-Man" only count behind the year or the quality.
            var tail = Technical().Match(release.RawTitle) is { Success: true } start ? release.RawTitle[start.Index..] : null;
            var edition = tail is null ? null : EditionTag().Match(tail) is { Success: true } match ? match.Value.Replace('.', ' ').Replace('_', ' ') : null;
            var group = tail is null && release.Resolution is null && release.Source == ReleaseSource.Unknown ? null : release.ReleaseGroup;
            var revision = Math.Clamp(release.Version > 1 ? release.Version : release.IsProper || release.IsRepack ? 2 : 1, 1, 9);
            if (quality is null && group is null && edition is null && revision == 1)
            {
                return null;
            }

            return new ReleaseFact(TextLimit.Cut(release.RawTitle, 500)!, TextLimit.Cut(group, 64), TextLimit.Cut(quality, 64), TextLimit.Cut(edition, 64), revision);
        }

        // A scanned file's own name unless its folder says more, as scene folders often do.
        public static ReleaseFact? OfPath(string relativePath)
        {
            var file = Of(Path.GetFileName(relativePath));
            var folder = Path.GetFileName(Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "") is { Length: > 0 } name ? Of(name) : null;
            return Score(folder) > Score(file) ? folder : file;
        }

        private static int Score(ReleaseFact? fact) => fact is null ? -1 : (fact.ReleaseGroup is null ? 0 : 2) + (fact.QualityKey is null ? 0 : 1) + (fact.Edition is null ? 0 : 1);

        [GeneratedRegex(@"\[([A-Za-z]+-\d{3,4}p)\]")]
        private static partial Regex QualityTag();

        [GeneratedRegex(@"(?<![0-9])(?:19|20)\d{2}(?![0-9])|(?<![0-9])\d{3,4}p(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
        private static partial Regex Technical();

        [GeneratedRegex(@"(?<![A-Za-z])(Director'?s[ ._]Cut|Extended(?:[ ._](?:Cut|Edition))?|Unrated|Uncut|Theatrical(?:[ ._]Cut)?|Remastered|IMAX|Special[ ._]Edition|Ultimate[ ._](?:Cut|Edition)|Criterion)(?![A-Za-z])", RegexOptions.IgnoreCase)]
        private static partial Regex EditionTag();
    }

    internal sealed record ReleaseFact(string ReleaseName, string? ReleaseGroup, string? QualityKey, string? Edition, int Revision);
}
