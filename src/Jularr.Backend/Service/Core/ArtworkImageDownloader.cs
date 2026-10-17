using System.Buffers;
using System.Net;
using SkiaSharp;

namespace Jularr.Backend.Service.Core
{
    // Provider image URLs are untrusted input even when metadata originally came from a known provider.
    // The HTTP client is configured with PublicNetwork.CreateHandler to prevent access to private networks.
    internal sealed class ArtworkImageDownloader(HttpClient client)
    {
        private const int MaximumDownloadBytes = 8 * 1024 * 1024;
        private const long MaximumPixels = 32_000_000;

        public async Task<byte[]?> DownloadJpegAsync(string sourceUrl, CancellationToken ct)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Port != 443)
            {
                return null;
            }

            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                for (int count; (count = await stream.ReadAsync(buffer, ct)) != 0;)
                {
                    if (memory.Length + count > MaximumDownloadBytes)
                    {
                        return null;
                    }

                    memory.Write(buffer, 0, count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var bytes = memory.ToArray();
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec is null || codec.Info.Width is < 1 or > 8000 || codec.Info.Height is < 1 or > 8000 ||
                (long)codec.Info.Width * codec.Info.Height > MaximumPixels)
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return jpeg?.ToArray();
        }
    }
}
