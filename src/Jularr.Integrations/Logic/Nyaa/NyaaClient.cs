using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Jularr.Integrations.Data.Nyaa;

namespace Jularr.Integrations.Logic.Nyaa
{
    // The torrent index answers a search in its RSS feed; every entry names the file that carries the torrent.
    internal sealed class NyaaClient(HttpClient http)
    {
         // Download from the same configured index host as the RSS item; do not redirect to a different provider.
        private const string DefaultIndex = "https://nyaa.si";

        // The literature of the index: English translated, the other languages and the raw text.
        public static readonly string[] LiteratureCategories = ["3_1", "3_2", "3_3"];

        // The file with the torrent of an entry; the number of the entry names it.
        public static Uri TorrentUri(string id) => new(DefaultIndex + "/download/" + Uri.EscapeDataString(id) + ".torrent");

        public static Uri TorrentUri(string id, string viewUri) =>
            new(new Uri(viewUri).GetLeftPart(UriPartial.Authority) + "/download/" + Uri.EscapeDataString(id) + ".torrent");

        // The page of an entry; it shows what the file holds and the magnet to it.
        public static Uri ViewUri(string id) => new(DefaultIndex + "/view/" + Uri.EscapeDataString(id));

        // Every category is asked in turn; the search of a language brings its own file.
        public async Task<IReadOnlyList<NyaaEntry>> SearchAsync(Uri site, string query, IReadOnlyCollection<string> categories, CancellationToken ct)
        {
            var found = new List<NyaaEntry>();
            foreach (var category in categories)
            {
                found.AddRange(await CategoryAsync(site, query, category, ct));
            }

            return found;
        }

        private async Task<IReadOnlyList<NyaaEntry>> CategoryAsync(Uri site, string query, string category, CancellationToken ct)
        {
            var address = new Uri(site, "?page=rss&c=" + Uri.EscapeDataString(category) + "&q=" + Uri.EscapeDataString(query));
            using var response = await http.GetAsync(address, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new NyaaException("The torrent index answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            XDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(xml);
            }
            catch (XmlException exception)
            {
                throw new NyaaException("The torrent index returned invalid XML.", exception);
            }

            var found = new List<NyaaEntry>();
            foreach (var item in document.Descendants("item"))
            {
                var title = item.Element("title")?.Value;
                var view = item.Element("guid")?.Value;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(view) || !Uri.TryCreate(view, UriKind.Absolute, out var page))
                {
                    continue;
                }

                // The number of the entry stands at the end of its address.
                var id = page.Segments.LastOrDefault()?.Trim('/');
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                found.Add(new NyaaEntry(id, title.Trim(), view.Trim(),
                    Named(item, "categoryId") ?? category,
                    Named(item, "categoryName") ?? Named(item, "category") ?? string.Empty,
                    Bytes(Named(item, "size")),
                    Number(Named(item, "seeders")),
                    Date(item.Element("pubDate")?.Value),
                    ReadDownloadLink(item.Element("link")?.Value)));
            }

            return found;
        }

        // Mirrors may use different XML namespaces for nyaa:* metadata.
        private static string? Named(XElement item, string name) =>
            item.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

        private static string? ReadDownloadLink(string? link) =>
            Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                ? uri.ToString() : null;

        // The size is written the way the index writes it: a number and its unit.
        private static long Bytes(string? size)
        {
            if (size is null)
            {
                return 0;
            }

            var parts = size.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            var factor = parts[1] switch
            {
                "KiB" => 1024d,
                "MiB" => 1024d * 1024,
                "GiB" => 1024d * 1024 * 1024,
                "TiB" => 1024d * 1024 * 1024 * 1024,
                _ => 0d
            };

            return factor == 0 ? 0 : (long)(value * factor);
        }

        private static int Number(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

        private static DateTimeOffset Date(string? value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var moment) ? moment : DateTimeOffset.MinValue;
    }
}
