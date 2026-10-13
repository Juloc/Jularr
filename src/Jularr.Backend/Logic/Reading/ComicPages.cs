using System.Globalization;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // Comic archives are image folders: pages in natural order, typed by their bytes.
    internal static partial class ComicPages
    {
        public const int AnalyzerVersion = 1;
        public const int MaximumPages = 5000;
        public const long MaximumPageBytes = 64L * 1024 * 1024;
        public const long MaximumVolumeBytes = 4L * 1024 * 1024 * 1024;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif" };

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        public static bool IsComic(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() is ".cbz" or ".cbr" or ".cb7";

        public static bool IsZip(string relativePath) => Path.GetExtension(relativePath).Equals(".cbz", StringComparison.OrdinalIgnoreCase);

        public static string PagePath(int index) => ":page/" + index.ToString("D5", CultureInfo.InvariantCulture);

        // Images outside metadata folders and hidden files.
        public static bool IsPage(string entryPath)
        {
            var path = entryPath.Replace('\\', '/');
            var name = path[(path.LastIndexOf('/') + 1)..];
            return name.Length > 0 && !name.StartsWith('.') && !path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                && ImageExtensions.Contains(Path.GetExtension(name));
        }

        // "page2" before "page10": numbers compare by value.
        public static string SortKey(string path) => Digits().Replace(path.Replace('\\', '/').ToLowerInvariant(), match => match.Value.TrimStart('0').PadLeft(12, '0'));

        // The content type its signature proves; anything else is no page.
        public static string? ContentType(ReadOnlySpan<byte> header) => header switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
            [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            [_, _, _, _, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66, ..] => "image/avif",
            _ => null
        };
    }
}
