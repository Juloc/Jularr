using System.Net;
using System.Net.Sockets;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Media
{
    // Where a provider's image host is replaced by a mirror, by host name.
    internal sealed class CoverMirrors(IReadOnlyDictionary<string, Uri> hosts)
    {
        public IReadOnlyDictionary<string, Uri> Hosts { get; } = hosts;
    }

    // An image a provider named, as the provider gave it: HTTPS from the providers' image hosts (or a configured mirror), public addresses only, bounded in size and time.
    internal sealed class CoverClient(HttpClient http, CoverMirrors mirrors)
    {
        public const int MaximumBytes = 20 * 1024 * 1024;
        private const int MaximumRedirects = 4;

        // The hosts of the image servers behind the metadata providers; the Internet Archive serves Open Library's and the Cover Art Archive's covers.
        private static readonly string[] ProviderHosts = ["tmdb.org", "coverartarchive.org", "openlibrary.org", "archive.org", "anilist.co", "anili.st", "igdb.com"];

        public async Task<(byte[] Bytes, string ContentType)> DownloadAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.UserInfo.Length != 0)
            {
                throw new InvalidDataException("The image address is not valid.");
            }

            // A mirror stands in for the provider's own host; anything else must be a known image host over HTTPS.
            var first = Mirrored(address);
            if (first is null && !IsProviderAddress(address))
            {
                throw new InvalidDataException("The image does not come from a provider.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var target = first ?? address;
            for (var hop = 0; ; hop++)
            {
                using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                // Redirects are followed by hand, each one to HTTPS.
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (hop >= MaximumRedirects || location is null || !Uri.TryCreate(target, location, out var next)
                        || next.Scheme != Uri.UriSchemeHttps && Mirrored(address) is null || next.UserInfo.Length != 0)
                    {
                        throw new InvalidDataException("The image moved somewhere it must not be fetched from.");
                    }

                    target = next;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumBytes)
                {
                    throw new InvalidDataException("The image is too large.");
                }

                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(chunk, timeout.Token)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes)
                    {
                        throw new InvalidDataException("The image is too large.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                var bytes = buffer.ToArray();
                return (bytes, Recognize(bytes) ?? throw new InvalidDataException("The answer is not an image."));
            }
        }

        // An HTTPS address on one of the providers' image hosts.
        public static bool IsProviderAddress(Uri address) =>
            address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && address.UserInfo.Length == 0
            && ProviderHosts.Any(host => address.Host == host || address.Host.EndsWith("." + host, StringComparison.Ordinal));

        public static bool IsProviderAddress(string url) => Uri.TryCreate(url, UriKind.Absolute, out var address) && IsProviderAddress(address);

        // What the bytes are, by their signature and never by what the server says.
        public static string? Recognize(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 32)
            {
                return null;
            }

            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                return "image/png";
            }

            if (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
            {
                return "image/gif";
            }

            if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            {
                return "image/webp";
            }

            return bytes[4..8].SequenceEqual("ftyp"u8) && (bytes[8..12].SequenceEqual("avif"u8) || bytes[8..12].SequenceEqual("avis"u8)) ? "image/avif" : null;
        }

        public static string ContentType(string extension) => extension.TrimStart('.') switch
        {
            "jpg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => "image/avif"
        };

        public static string Extension(string contentType) => contentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => "avif"
        };

        private Uri? Mirrored(Uri address) =>
            mirrors.Hosts.TryGetValue(address.Host, out var mirror) ? new Uri(mirror, address.PathAndQuery.TrimStart('/')) : null;

        // Connections go only to public addresses; a configured mirror may also be on this machine.
        public static SocketsHttpHandler CreateHandler(CoverMirrors mirrors) => new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var isMirror = mirrors.Hosts.Values.Any(mirror => mirror.Host == context.DnsEndPoint.Host && mirror.Port == context.DnsEndPoint.Port);
                var address = addresses.FirstOrDefault(item => isMirror || PublicNetwork.IsPublic(item)) ?? throw new HttpRequestException("The image address is not public.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}
