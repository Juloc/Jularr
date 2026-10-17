using Jularr.Backend.Data.Enums.Media;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Logic.Media
{
    // Export only into a real Work directory, never the shared root of a library.
    internal static class ArtworkExportDirectory
    {
        public static string? For(MediaType mediaType, string mediaPath)
        {
            var directory = StoragePath.Parent(mediaPath);
            if (directory.Length == 0)
            {
                return null;
            }

            if (mediaType == MediaType.Series)
            {
                var last = StoragePath.Name(directory);
                var normalized = last.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                if (normalized == "specials" ||
                    normalized.StartsWith("season", StringComparison.Ordinal) && int.TryParse(normalized.AsSpan(6), out _) ||
                    normalized.StartsWith("s", StringComparison.Ordinal) && int.TryParse(normalized.AsSpan(1), out _))
                {
                    directory = StoragePath.Parent(directory);
                }
            }

            return directory.Length == 0 ? null : directory;
        }
    }
}
