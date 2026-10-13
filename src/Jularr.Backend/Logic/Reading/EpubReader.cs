using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data.Logic.Reading;

namespace Jularr.Backend.Logic.Reading
{
    // EPUB 2 and 3 packages from a seekable archive; bounded and path-safe.
    internal static class EpubReader
    {
        public const int MaximumEntries = 10_000;
        public const long MaximumEntryBytes = 64L * 1024 * 1024;
        public const long MaximumTotalBytes = 512L * 1024 * 1024;

        private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
        private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
        private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

        public static EpubPackage Read(ZipArchive archive)
        {
            // Container checks before any parsing.
            if (archive.Entries.Count > MaximumEntries || archive.Entries.Any(entry => entry.Length > MaximumEntryBytes) || archive.Entries.Sum(entry => entry.Length) > MaximumTotalBytes)
            {
                throw new InvalidDataException("The EPUB exceeds the size limits.");
            }

            if (archive.GetEntry("mimetype") is not { } mimetype || ReadText(mimetype).Trim() != "application/epub+zip")
            {
                throw new InvalidDataException("The file is not an EPUB.");
            }

            var container = ReadXml(archive, "META-INF/container.xml");
            var packagePath = Normalize("", container.Descendants(Container + "rootfile").Select(root => (string?)root.Attribute("full-path")).FirstOrDefault(path => path is not null))
                ?? throw new InvalidDataException("The EPUB has no package document.");
            var package = ReadXml(archive, packagePath);
            var directory = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";

            // Manifest, spine and metadata.
            var manifest = new Dictionary<string, EpubItem>(StringComparer.Ordinal);
            foreach (var item in package.Descendants(Opf + "item"))
            {
                if ((string?)item.Attribute("id") is { } id && Normalize(directory, (string?)item.Attribute("href")) is { } path)
                {
                    manifest[id] = new EpubItem(path, ((string?)item.Attribute("media-type") ?? "application/octet-stream").Trim(), ((string?)item.Attribute("properties") ?? "").Split(' '));
                }
            }

            var spine = package.Descendants(Opf + "itemref")
                .Where(reference => (string?)reference.Attribute("linear") != "no")
                .Select(reference => manifest.GetValueOrDefault((string?)reference.Attribute("idref") ?? ""))
                .OfType<EpubItem>()
                .Where(item => item.MediaType is "application/xhtml+xml" or "text/html" && archive.GetEntry(item.Path) is not null)
                .DistinctBy(item => item.Path)
                .ToArray();
            if (spine.Length == 0)
            {
                throw new InvalidDataException("The EPUB has no readable sections.");
            }

            var metadata = package.Descendants(Opf + "metadata").FirstOrDefault();
            var title = Clean(metadata?.Element(Dc + "title")?.Value, 500);
            var language = Clean(metadata?.Element(Dc + "language")?.Value, 35);

            // EPUB 3 navigation first, then the EPUB 2 NCX.
            var toc = new List<EpubTocItem>();
            if (manifest.Values.FirstOrDefault(item => item.Properties.Contains("nav")) is { } nav && archive.GetEntry(nav.Path) is { } navEntry)
            {
                ReadNavigation(ReadText(navEntry), nav.Path, toc);
            }

            var ncxId = (string?)package.Descendants(Opf + "spine").FirstOrDefault()?.Attribute("toc");
            if (toc.Count == 0 && (ncxId is null ? manifest.Values.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml") : manifest.GetValueOrDefault(ncxId)) is { } ncx)
            {
                ReadNcx(ReadXml(archive, ncx.Path), ncx.Path, toc);
            }

            return new EpubPackage(title, language, spine, toc, manifest.Values.DistinctBy(item => item.Path).ToDictionary(item => item.Path, item => item.MediaType, StringComparer.Ordinal));
        }

        public static string ReadText(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static byte[] ReadBytes(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, MaximumEntryBytes));
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        // Archive paths stay inside the archive; null for external or broken references.
        public static string? Normalize(string directory, string? href)
        {
            if (string.IsNullOrWhiteSpace(href) || href.Contains(':') || href.StartsWith('/') || href.StartsWith('\\'))
            {
                return null;
            }

            var target = Uri.UnescapeDataString(href.Split('#', 2)[0].Split('?', 2)[0]);
            if (target.Length == 0)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var part in (directory + target).Replace('\\', '/').Split('/'))
            {
                if (part is "" or ".") continue;
                if (part == "..")
                {
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(part);
            }

            return parts.Count == 0 ? null : string.Join('/', parts);
        }

        public static string DirectoryOf(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

        public static string? Anchor(string? href) => href?.Split('#', 2) is [_, { Length: > 0 and <= 256 } anchor] ? anchor : null;

        private static void ReadNavigation(string html, string navPath, List<EpubTocItem> toc)
        {
            var document = new HtmlParser().ParseDocument(html);
            var nav = document.QuerySelectorAll("nav").FirstOrDefault(element => element.GetAttribute("epub:type") == "toc") ?? document.QuerySelector("nav");
            if (nav?.QuerySelector("ol") is { } list)
            {
                Walk(list, 0);
            }

            void Walk(AngleSharp.Dom.IElement ordered, int level)
            {
                foreach (var item in ordered.Children.Where(child => child.LocalName == "li"))
                {
                    var link = item.Children.FirstOrDefault(child => child.LocalName is "a" or "span");
                    var href = link?.GetAttribute("href");
                    if (Clean(link?.TextContent, 500) is { } title && Normalize(DirectoryOf(navPath), href) is { } path)
                    {
                        toc.Add(new EpubTocItem(title, path, Anchor(href), Math.Min(level, 10)));
                    }

                    if (item.Children.FirstOrDefault(child => child.LocalName == "ol") is { } nested && toc.Count < MaximumEntries)
                    {
                        Walk(nested, level + 1);
                    }
                }
            }
        }

        private static void ReadNcx(XDocument ncx, string ncxPath, List<EpubTocItem> toc)
        {
            void Walk(XElement parent, int level)
            {
                foreach (var point in parent.Elements(Ncx + "navPoint"))
                {
                    var href = (string?)point.Element(Ncx + "content")?.Attribute("src");
                    if (Clean(point.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value, 500) is { } title && Normalize(DirectoryOf(ncxPath), href) is { } path)
                    {
                        toc.Add(new EpubTocItem(title, path, Anchor(href), Math.Min(level, 10)));
                    }

                    if (toc.Count < MaximumEntries)
                    {
                        Walk(point, level + 1);
                    }
                }
            }

            if (ncx.Root?.Element(Ncx + "navMap") is { } map)
            {
                Walk(map, 0);
            }
        }

        private static XDocument ReadXml(ZipArchive archive, string path)
        {
            // No DTDs, no external entities.
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException("The EPUB is incomplete.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumEntryBytes });
            try
            {
                return XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException("The EPUB contains invalid XML.", exception);
            }
        }

        private static string? Clean(string? value, int length)
        {
            var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 ? null : text.Length <= length ? text : text[..length];
        }
    }
}
