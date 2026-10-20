using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    // The Admin's patterns and naming rules as imports and renames use them: a subtype's own pattern, else its type's.
    internal sealed class NamingSet(IReadOnlyDictionary<(MediaType Type, MediaSubtype? Subtype), string> patterns, IReadOnlyDictionary<MediaType, NamingRules>? rules = null)
    {
        public string Pattern(MediaType mediaType, MediaSubtype? subtype) =>
            subtype is not null && patterns.TryGetValue((mediaType, subtype), out var own) ? own : patterns[(mediaType, null)];

        public NamingRules Rules(MediaType mediaType) => rules?.GetValueOrDefault(mediaType) ?? NamingRules.Default;

        public IEnumerable<string> Patterns => patterns.Values;

        // Tests and callers with one pattern per type.
        public static implicit operator NamingSet(Dictionary<MediaType, string> byType) =>
            new(byType.ToDictionary(item => (item.Key, (MediaSubtype?)null), item => item.Value));
    }
}
