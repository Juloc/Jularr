using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Storage
{
    // Stable operation-owned paths, independent of source provider and download protocol.
    internal static class DownloadWorkspaceLayout
    {
        private static readonly string[] Categories = ["movies", "tv", "books", "anime", "manga", "lightnovels", "music", "games"];

        public static string Category(MediaType mediaType, MediaSubtype? subtype)
        {
            if (!MediaSubtypes.Fits(mediaType, subtype)) throw new ArgumentException("The media subtype does not belong to this type.", nameof(subtype));
            return subtype switch
            {
                MediaSubtype.Anime => "anime",
                MediaSubtype.Manga => "manga",
                MediaSubtype.LightNovel => "lightnovels",
                _ => mediaType switch
                {
                    MediaType.Movie => "movies",
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

        // Only explicitly named job directories are eligible for native cleanup.
        public static IEnumerable<string> ExistingJobCandidates(long operationId)
        {
            yield return operationId.ToString(CultureInfo.InvariantCulture);
            var name = JobName(operationId);
            foreach (var category in Categories) yield return Path.Combine(category, name);
        }
    }
