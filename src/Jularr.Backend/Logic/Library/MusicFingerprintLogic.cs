namespace Jularr.Backend.Logic.Library
{
    internal static class MusicFingerprintLogic
    {
        // The one release group every file's fingerprint matches; none or several leave the files for a person.
        public static string? Album(IReadOnlyList<IReadOnlyList<string>> matches)
        {
            if (matches.Count == 0)
            {
                return null;
            }

            var common = matches.Skip(1).Aggregate(matches[0].ToHashSet(StringComparer.Ordinal), (set, next) =>
            {
                set.IntersectWith(next);
                return set;
            });
            return common.Count == 1 ? common.First() : null;
        }
    }
}
