using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // Books and comics open in the reader, everything else in the player.
    public static class ReadableMedia
    {
        public static bool IsReadable(MediaAssetType type) => type is MediaAssetType.Ebook or MediaAssetType.Comic;
    }
}
