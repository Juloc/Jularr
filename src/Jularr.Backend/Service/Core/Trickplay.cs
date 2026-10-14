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

        public static string Folder(string root, long storedFileId) => Path.Combine(root, storedFileId.ToString(CultureInfo.InvariantCulture));

        public static string Sheet(string folder, int sheet) => Path.Combine(folder, sheet.ToString("000", CultureInfo.InvariantCulture) + ".jpg");
    }
}
