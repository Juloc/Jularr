using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // A subtype names the Work's list; otherwise its type does.
    public static class MediaTypeLabel
    {
        public static string Key(MediaType type, MediaSubtype? subtype) => subtype is { } value ? "media.subtype." + value : "media.type." + type;
    }
}
