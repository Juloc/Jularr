using System.Security;
using System.Xml.Linq;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace Jularr.Frontend.Modules.Pwa
{
    // A deterministic image has no account ID or authentication data in the URL or pixel content.
    // The account-specific manifest and Apple tag only choose which public, color-addressed rendition to show.
    public static class PwaIconRenderer
    {
        private const float ViewBoxSize = 924.7f;
        private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 64 });

        private static readonly Lazy<SKPath> Mark = new(() =>
        {
            using var stream = typeof(PwaIconRenderer).Assembly.GetManifestResourceStream("Jularr.Frontend.Pages.Shared.brand-clean.svg")
                ?? throw new InvalidOperationException("The canonical parrot artwork is missing.");
            var svg = XDocument.Load(stream);
            var data = svg.Root?.Element(XName.Get("path", "http://www.w3.org/2000/svg"))?.Attribute("d")?.Value
                ?? throw new InvalidOperationException("The parrot path is missing.");
            var path = SKPath.ParseSvgPathData(data) ?? throw new InvalidOperationException("The parrot path is invalid.");
            path.FillType = SKPathFillType.EvenOdd;
            return path;
        });

        public static bool IsValid(string kind, string color) =>
            kind is "192" or "512" or "maskable-512" or "apple-180"
            && color.Length == 6 && color.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

        public static string IconUrl(string kind, string color) => "/pwa/icons/" + kind + "/" + color + ".png";

        public static byte[] Render(string kind, string color)
        {
            if (!IsValid(kind, color)) throw new ArgumentException("Invalid PWA icon.", nameof(color));
            var key = kind + ":" + color;
            if (Cache.TryGetValue(key, out byte[]? cached) && cached is not null) return cached;

            var size = kind switch { "192" => 192, "apple-180" => 180, _ => 512 };
            using var bitmap = new SKBitmap(size, size);
            using var canvas = new SKCanvas(bitmap);
            // Nontransparent rectangular artwork: the operating system applies the final rounded mask.
            canvas.Clear(SKColors.White);
            // Keep the entire mark inside the Android 80%-diameter safe circle, including its farthest corners.
            var fraction = kind == "maskable-512" ? 0.54f : 0.68f;
            var scale = fraction * size / ViewBoxSize;
            var margin = (size - fraction * size) / 2f;
            canvas.Translate(margin, margin);
            canvas.Scale(scale);
            using var paint = new SKPaint
            {
                Color = SKColor.Parse("#" + color),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawPath(Mark.Value, paint);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
            var result = bytes.ToArray();
            // Public, color-only variants may be cached internally but the cache is strictly size bounded.
            Cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
            return result;
        }

        public static async Task<string?> AccountAccentAsync(HttpContext http)
        {
            if (http.User.Identity?.IsAuthenticated != true) return null;
            var service = http.RequestServices.GetService<ReadPwaAppearanceV1>();
            if (service is null) return null;

            try
            {
                var appearance = await service.ExecuteAsync<PwaAppearanceV1>(cancellationToken: http.RequestAborted);
                return appearance.MainProfileId is null || appearance.AccentHex is not { Length: 7 } accent
                    ? null : accent[1..];
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                // Expired or revoked accounts get the neutral icon, never another account's stored color.
                return null;
            }
        }
    }
}
