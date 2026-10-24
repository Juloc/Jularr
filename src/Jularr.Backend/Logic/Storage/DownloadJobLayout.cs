using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Storage
{
    // The same relative job path is used in incomplete and complete, regardless of downloader.
    internal static class DownloadJobLayout
    {
        public static string RelativePath(long operationId, MediaType mediaType, MediaSubtype? mediaSubtype)
        {
            if (operationId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(operationId));
            }

            if (!MediaSubtypes.Fits(mediaType, mediaSubtype))
            {
                throw new ArgumentException("The media subtype does not belong to the media type.", nameof(mediaSubtype));
            }

            var category = mediaSubtype switch
            {
                MediaSubtype.Anime => "anime",
                MediaSubtype.Manga => "manga",
                MediaSubtype.LightNovel => "lightnovel",
                null => mediaType switch
                {
                    MediaType.Movie => "movie",
                    MediaType.Series => "tv",
                    MediaType.Book => "books",
                    MediaType.Music => "music",
                    MediaType.Game => "games",
                    _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
                },
                _ => throw new ArgumentOutOfRangeException(nameof(mediaSubtype))
            };

            return Path.Combine(category, "j-" + operationId.ToString(CultureInfo.InvariantCulture));
        }
    }
}
