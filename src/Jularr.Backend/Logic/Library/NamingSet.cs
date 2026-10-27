using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    // The Admin's patterns and naming rules as imports and renames use them: a subtype's own pattern, else its type's.
    internal sealed class NamingSet(IReadOnlyDictionary<(MediaType Type, MediaSubtype? Subtype), string> patterns, IReadOnlyDictionary<MediaType, NamingRules>? rules = null,
        IReadOnlyDictionary<(MediaType Type, MediaSubtype? Subtype), string>? daily = null)
    {
        public string Pattern(MediaType mediaType, MediaSubtype? subtype) =>
            subtype is not null && patterns.TryGetValue((mediaType, subtype), out var own) ? own : patterns[(mediaType, null)];

        public string Pattern(MediaType mediaType, MediaSubtype? subtype, bool isDaily)
        {
            var pattern = Pattern(mediaType, subtype);
            if (!isDaily || subtype is not null || daily?.GetValueOrDefault((mediaType, null)) is not { } file) return pattern;
            return NamingPattern.Join(NamingPattern.Split(pattern).Folder, file);
        }

        public NamingRules Rules(MediaType mediaType) => rules?.GetValueOrDefault(mediaType) ?? NamingRules.Default;

        // Keeping alternative video releases requires video names that distinguish them.
        public IEnumerable<string> VideoPatterns => patterns.Where(item => item.Key.Type is MediaType.Movie or MediaType.Series).Select(item => item.Value)
            .Concat(daily?.Where(item => item.Key.Type == MediaType.Series).Select(item => item.Value) ?? []);

        // Tests and callers with one pattern per type.
        public static implicit operator NamingSet(Dictionary<MediaType, string> byType) =>
            new(byType.ToDictionary(item => (item.Key, (MediaSubtype?)null), item => item.Value));
    }
}
