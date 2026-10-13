using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Opds;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Opds
{
    // OPDS 1 catalogs (Calibre-Web, Standard Ebooks, Kavita and others) through their OpenSearch description.
    internal sealed class OpdsClient(HttpClient http)
    {
        public const long MaximumBytes = 500L * 1024 * 1024;
        private const long MaximumFeedBytes = 8L * 1024 * 1024;

        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
        private static readonly XNamespace Dc = "http://purl.org/dc/terms/";
        private static readonly XNamespace DcElements = "http://purl.org/dc/elements/1.1/";

        private static readonly Dictionary<string, string> Formats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/epub+zip"] = "epub",
            ["application/pdf"] = "pdf",
            ["application/x-mobipocket-ebook"] = "mobi",
            ["application/vnd.amazon.ebook"] = "azw3"
        };

        public async Task<IReadOnlyList<OpdsBook>> SearchAsync(OpdsConnection connection, string query, CancellationToken ct)
        {
            // The catalog names its search; the template receives the terms.
            var root = await ReadXmlAsync(connection, connection.Catalog, ct);
            var search = root.Root?.Elements(Atom + "link").FirstOrDefault(link => (string?)link.Attribute("rel") == "search")
                ?? throw new OpdsException("The catalog offers no search.");
            var href = Resolve(connection.Catalog, (string?)search.Attribute("href")) ?? throw new OpdsException("The catalog search link is invalid.");
            string template;
            if (((string?)search.Attribute("type"))?.Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase) == true)
            {
                var description = await ReadXmlAsync(connection, href, ct);
                template = description.Root?.Elements(OpenSearch + "Url")
                    .FirstOrDefault(url => ((string?)url.Attribute("type"))?.Contains("atom", StringComparison.OrdinalIgnoreCase) == true)?.Attribute("template")?.Value
                    ?? throw new OpdsException("The catalog search description has no Atom template.");
            }
            else
            {
                template = Uri.UnescapeDataString(href.ToString());
            }

            var target = Resolve(connection.Catalog, template.Replace("{searchTerms}", Uri.EscapeDataString(query), StringComparison.Ordinal)
                .Replace("{startPage?}", "1", StringComparison.Ordinal).Replace("{count?}", "50", StringComparison.Ordinal))
                ?? throw new OpdsException("The catalog search template is invalid.");
            var feed = await ReadXmlAsync(connection, target, ct);
            return [.. (feed.Root?.Elements(Atom + "entry") ?? []).Take(50).Select(entry => ReadEntry(target, entry)).OfType<OpdsBook>()];
        }

        public async Task DownloadAsync(OpdsConnection connection, Uri source, string destination, CancellationToken ct)
        {
            using var response = await DirectDownload.SendAsync(http, source, Credentials(connection), ct);
            await DirectDownload.SaveAsync(response, destination, MaximumBytes, ct);
        }

        private static OpdsBook? ReadEntry(Uri feed, XElement entry)
        {
            var title = entry.Element(Atom + "title")?.Value.Trim();
            var id = entry.Element(Atom + "id")?.Value.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(id))
            {
                return null;
            }

            // Acquisition links carry the formats Jularr can store.
            var links = entry.Elements(Atom + "link")
                .Where(link => ((string?)link.Attribute("rel"))?.StartsWith("http://opds-spec.org/acquisition", StringComparison.Ordinal) == true)
                .Select(link => (Uri: Resolve(feed, (string?)link.Attribute("href")), Format: Formats.GetValueOrDefault(((string?)link.Attribute("type") ?? "").Split(';')[0].Trim())))
                .Where(link => link.Uri is not null && link.Format is not null)
                .Select(link => new OpdsLink(link.Uri!, link.Format!))
                .ToArray();
            if (links.Length == 0)
            {
                return null;
            }

            var authors = entry.Elements(Atom + "author").Select(author => author.Element(Atom + "name")?.Value.Trim()).OfType<string>().Where(name => name.Length > 0).Take(3).ToArray();
            var language = (entry.Element(Dc + "language") ?? entry.Element(DcElements + "language"))?.Value.Trim();
            return new OpdsBook(id.Length <= 256 ? id : id[..256], title.Length <= 500 ? title : title[..500], authors, language, links);
        }

        private async Task<XDocument> ReadXmlAsync(OpdsConnection connection, Uri uri, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await DirectDownload.SendAsync(http, uri, Credentials(connection), ct);
            }
            catch (DirectDownloadException exception)
            {
                throw new OpdsException("The catalog could not be read.", exception);
            }

            using (response)
            {
                if (response.Content.Headers.ContentLength > MaximumFeedBytes)
                {
                    throw new OpdsException("The catalog feed is too large.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumFeedBytes, Async = true });
                    return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
                }
                catch (XmlException exception)
                {
                    throw new OpdsException("The catalog returned invalid XML.", exception);
                }
            }
        }

        private static DirectCredentials? Credentials(OpdsConnection connection) => connection.Username is { Length: > 0 } user
            ? new DirectCredentials(connection.Catalog, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + connection.Password))).ToString())
            : null;

        private static Uri? Resolve(Uri baseUri, string? href) =>
            string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var uri) || uri.Scheme is not ("http" or "https") ? null : uri;
    }
}
