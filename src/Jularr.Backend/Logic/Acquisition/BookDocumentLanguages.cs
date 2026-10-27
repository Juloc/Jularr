using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Only explicit EPUB package language tags are evidence; never infer a language from
    // title, author, filename, text samples or a PDF whose language is not documented.
    internal static class BookDocumentLanguages
    {
        private const long MaximumXmlBytes = 1024 * 1024;
        private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
        private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

        public static string[] ReadEpub(Stream input)
        {
            if (!input.CanSeek) return [];
            try
            {
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > EpubReader.MaximumEntries) return [];
                if (zip.GetEntry("mimetype") is not { Length: <= 128 } mime
                    || EpubReader.ReadText(mime).Trim() != "application/epub+zip")
                    return [];
                if (zip.GetEntry("META-INF/container.xml") is not { } container || container.Length > MaximumXmlBytes)
                    return [];
                var doc = ReadXml(container);
                var opfPath = EpubReader.Normalize("", doc.Descendants(Container + "rootfile")
                    .Select(element => (string?)element.Attribute("full-path")).FirstOrDefault(path => path is not null));
                if (opfPath is null || zip.GetEntry(opfPath) is not { } opf || opf.Length > MaximumXmlBytes)
                    return [];

                var package = ReadXml(opf);
                return [.. package.Descendants(Opf + "metadata").Elements(Dc + "language")
                    .Select(element => LanguageCodes.ToTwoLetter(element.Value.Trim())?.ToUpperInvariant())
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or ArgumentException)
            {
                // A document with unreadable/absent metadata is unknown, not assumed English.
                return [];
            }
        }

        private static XDocument ReadXml(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes
            });
            return XDocument.Load(reader);
        }

        // Unknown EPUB metadata cannot satisfy a mandatory-language promise.
        // Preserve the download for manual confirmation; do not invent a language.
        public static string? StrictEpubAttentionReason(QualityProfile profile, IReadOnlyList<string> languages)
        {
            if (!profile.IsBook || profile.LanguageFallback != Data.Enums.Acquisition.LanguageFallbackType.None
                || profile.LanguageOrder.Length == 0)
                return null;
            return languages.Count == 0 ? "import_book_language_unknown"
                : ContradictsStrictProfile(profile, languages) ? "import_book_language_mismatch" : null;
        }

        public static bool ContradictsStrictProfile(QualityProfile profile, IReadOnlyList<string> verifiedLanguages) =>
            profile.IsBook && profile.LanguageFallback == Data.Enums.Acquisition.LanguageFallbackType.None
                && profile.LanguageOrder.Length > 0 && verifiedLanguages.Count > 0
                && !UpgradePolicy.IsRequested(profile, verifiedLanguages);
    }
}
