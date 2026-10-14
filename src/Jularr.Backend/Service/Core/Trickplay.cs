using System.Globalization;

namespace Jularr.Backend.Service.Core
{
    // Layout of the seek thumbnails shared by the renderer and the player services.
    internal static class Trickplay
    {
        public const int IntervalSeconds = 10;
        public const int Columns = 10;
        public const int Rows = 10;
        public const int TileWidth = 160;

        // Even, as the encoder needs it.
        public static int TileHeight(int videoWidth, int videoHeight) => Math.Clamp((int)Math.Round(TileWidth * (double)videoHeight / videoWidth / 2) * 2, 2, 4096);

        public static string Folder(string root, long storedFileId) => Path.Combine(root, storedFileId.ToString(CultureInfo.InvariantCulture));

        public static string Sheet(string folder, int sheet) => Path.Combine(folder, sheet.ToString("000", CultureInfo.InvariantCulture) + ".jpg");
    }
}
