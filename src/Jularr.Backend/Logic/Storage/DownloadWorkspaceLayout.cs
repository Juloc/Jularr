using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Storage
{
    // Stable operation-owned paths, independent of source provider and download protocol.
    internal static class DownloadWorkspaceLayout
    {
        private static readonly string[] Categories = ["movie", "tv", "books", "anime", "manga", "lightnovel", "music", "games"];

        public static string Category(MediaType mediaType, MediaSubtype? subtype)
        {
            if (!MediaSubtypes.Fits(mediaType, subtype)) throw new ArgumentException("The media subtype does not belong to this type.", nameof(subtype));
            return subtype switch
            {
                MediaSubtype.Anime => "anime",
                MediaSubtype.Manga => "manga",
                MediaSubtype.LightNovel => "lightnovel",
                _ => mediaType switch
                {
                    MediaType.Movie => "movie",
                    MediaType.Series => "tv",
                    MediaType.Book => "books",
                    MediaType.Music => "music",
                    MediaType.Game => "games",
                    _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
                }
            };
        }

        public static string JobName(long operationId) => operationId > 0
            ? "j-" + operationId.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(operationId));

        public static string Relative(long operationId, MediaType mediaType, MediaSubtype? subtype) => Path.Combine(Category(mediaType, subtype), JobName(operationId));

        // External downloaders may use another OS or container. Join their path using
        // that client's separator, never Path.Combine() from Jularr's own host.
        public static string ExternalJobDirectory(string clientRoot, long operationId, MediaType mediaType, MediaSubtype? subtype)
        {
            if (string.IsNullOrWhiteSpace(clientRoot)) throw new ArgumentException("A client save root is required.", nameof(clientRoot));
            var root = clientRoot.TrimEnd('/', '\\');
            if (root.Length == 0) throw new ArgumentException("A client save root cannot be the filesystem root.", nameof(clientRoot));
            var separator = clientRoot.Contains('\\') && !clientRoot.Contains('/') ? "\\" : "/";
            return root + separator + Category(mediaType, subtype) + separator + JobName(operationId);
        }

        // Only explicitly named job directories are eligible for native cleanup.
        public static IEnumerable<string> ExistingJobCandidates(long operationId)
        {
            yield return operationId.ToString(CultureInfo.InvariantCulture);
            var name = JobName(operationId);
            foreach (var category in Categories) yield return Path.Combine(category, name);
        }
    }
}
