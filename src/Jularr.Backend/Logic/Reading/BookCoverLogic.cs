using System.IO.Compression;
using System.Xml.Linq;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Logic.Reading
{
    // The cover a book file carries itself: an EPUB names it in its package, a comic archive starts with it.
    internal static class BookCoverLogic
    {
        private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
        private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

        // The EPUB's cover image: the item marked "cover-image" (EPUB 3), else the one the "cover" meta names (EPUB 2), else an image item called "cover".
        public static (byte[] Bytes, string ContentType)? FromEpub(ZipArchive archive)
        {
            if (archive.GetEntry("META-INF/container.xml") is not { } containerEntry || containerEntry.Length > EpubReader.MaximumEntryBytes)
            {
                return null;
            }

            var container = EpubReader.LoadXml(EpubReader.ReadBytes(containerEntry), EpubReader.MaximumEntryBytes);
            if (EpubReader.Normalize("", container.Descendants(Container + "rootfile").Select(root => (string?)root.Attribute("full-path")).FirstOrDefault(path => path is not null)) is not { } packagePath
                || archive.GetEntry(packagePath) is not { } packageEntry || packageEntry.Length > EpubReader.MaximumEntryBytes)
            {
                return null;
            }

            var package = EpubReader.LoadXml(EpubReader.ReadBytes(packageEntry), EpubReader.MaximumEntryBytes);
            var directory = EpubReader.DirectoryOf(packagePath);
            var items = package.Descendants(Opf + "item")
                .Select(item => (Id: (string?)item.Attribute("id"), Href: (string?)item.Attribute("href"), Type: ((string?)item.Attribute("media-type") ?? "").Trim(),
                    Properties: ((string?)item.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Where(item => item.Type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var named = (string?)package.Descendants(Opf + "meta").FirstOrDefault(meta => (string?)meta.Attribute("name") == "cover")?.Attribute("content");
            var chosen = items.Where(item => item.Properties.Contains("cover-image")).Concat(items.Where(item => named is not null && item.Id == named))
                .Concat(items.Where(item => item.Id is "cover" or "cover-image" or "coverimage")).Select(item => item.Href);
            foreach (var href in chosen)
            {
                if (EpubReader.Normalize(directory, href) is { } path && archive.GetEntry(path) is { } entry && Read(entry) is { } cover)
                {
                    return cover;
                }
            }

            return null;
        }

        // The first page of a comic archive is its cover.
        public static (byte[] Bytes, string ContentType)? FromComic(ZipArchive archive)
        {
            var page = archive.Entries.Where(entry => ComicPages.IsPage(entry.FullName)).OrderBy(entry => ComicPages.SortKey(entry.FullName), StringComparer.Ordinal).FirstOrDefault();
            return page is null ? null : Read(page);
        }

        // An image of a size a cover can have, typed by its bytes.
        private static (byte[] Bytes, string ContentType)? Read(ZipArchiveEntry entry)
        {
            if (entry.Length is 0 or > CoverClient.MaximumBytes)
            {
                return null;
            }

            var bytes = EpubReader.ReadBytes(entry);
            return CoverClient.Recognize(bytes) is { } type ? (bytes, type) : null;
        }
    }
}
