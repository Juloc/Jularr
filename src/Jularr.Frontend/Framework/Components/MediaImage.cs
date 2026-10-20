using System.Text.Encodings.Web;
using Jularr.Backend.Logic.Media;

namespace Jularr.Frontend.Framework
{
    public sealed record ImageVariant(string Url, int Width);

    // Image URLs and title/alt text must come from an authorized Backend read.
    // The Backend/image endpoint owns resizing, ETag and access control.
    public sealed class MediaImage(
        string url, string alt, int width, int height,
        IReadOnlyList<ImageVariant>? variants = null, string? sizes = null,
        bool priority = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            ValidateUrl(url);

            // A cover Jularr keeps comes in a few widths; the browser takes the one it needs.
            var source = CoverPaths.Sized(url, width);
            if (variants is null && source != url)
            {
                variants = [.. CoverPaths.Variants(url).Select(variant => new ImageVariant(variant.Url, variant.Width))];
                sizes ??= width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
            }

            var image = new Img()
                .Class("media-image")
                .Attribute("src", source)
                .Attribute("alt", alt)
                .Attribute("width", width.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Attribute("height", height.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Attribute("decoding", "async")
                .Attribute("loading", priority ? "eager" : "lazy");

            if (priority) image.Attribute("fetchpriority", "high");

            if (variants is { Count: > 0 })
            {
                foreach (var variant in variants)
                {
                    if (variant.Width <= 0) throw new ArgumentOutOfRangeException(nameof(variants));
                    ValidateUrl(variant.Url);
                }

                image.Attribute("srcset", string.Join(", ", variants.Select(v =>
                    $"{v.Url} {v.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)}w")));
                image.Attribute("sizes", sizes ?? "100vw");
            }

            image.WriteTo(writer, encoder, texts);
        }

        private static void ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl)
                || url.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException("Invalid image URL.");

            if (url[0] == '/' && url.Length > 1) return;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.UserInfo.Length == 0)
                return;

            throw new ArgumentException("Only root-relative or HTTP(S) media image URLs are permitted.");
        }
    }
}
