using System.IO.Compression;

namespace Jularr.Backend.Logic.Reading
{
    // The pages of a comic archive: its image entries, as the reader would show them; nothing else is counted.
    internal static class PageCounter
    {
        public static int? Count(Stream input, string relativePath)
        {
            if (!ComicPages.IsZip(relativePath))
            {
                return null;
            }

            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var pages = archive.Entries.Count(entry => ComicPages.IsPage(entry.FullName));
            return pages == 0 ? null : Math.Min(pages, ComicPages.MaximumPages);
        }
    }
}
