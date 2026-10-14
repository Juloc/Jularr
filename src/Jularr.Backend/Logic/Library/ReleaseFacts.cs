using System.Text.RegularExpressions;
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

            var quality = ReleaseQuality.GetKey(release);
            var edition = EditionTag().Match(release.RawTitle);
            var revision = Math.Clamp(release.Version > 1 ? release.Version : release.IsProper || release.IsRepack ? 2 : 1, 1, 9);
            return new ReleaseFact(Cut(release.RawTitle, 500)!, Cut(release.ReleaseGroup, 64), quality.Contains("UNKNOWN", StringComparison.Ordinal) ? null : Cut(quality, 64),
                edition.Success ? Cut(edition.Value.Replace('.', ' ').Replace('_', ' '), 64) : null, revision);
        }

        // A scanned file's own name unless its folder says more, as scene folders often do.
        public static ReleaseFact? OfPath(string relativePath)
        {
            var file = Of(Path.GetFileName(relativePath));
            var folder = Path.GetFileName(Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "") is { Length: > 0 } name ? Of(name) : null;
            return Score(folder) > Score(file) ? folder : file;
        }

        private static int Score(ReleaseFact? fact) => fact is null ? -1 : (fact.ReleaseGroup is null ? 0 : 2) + (fact.QualityKey is null ? 0 : 1) + (fact.Edition is null ? 0 : 1);

        private static string? Cut(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

        [GeneratedRegex(@"(?<![A-Za-z])(Director'?s[ ._]Cut|Extended(?:[ ._](?:Cut|Edition))?|Unrated|Uncut|Theatrical(?:[ ._]Cut)?|Remastered|IMAX|Special[ ._]Edition|Ultimate[ ._](?:Cut|Edition)|Criterion)(?![A-Za-z])", RegexOptions.IgnoreCase)]
        private static partial Regex EditionTag();
    }

    internal sealed record ReleaseFact(string ReleaseName, string? ReleaseGroup, string? QualityKey, string? Edition, int Revision);
}
