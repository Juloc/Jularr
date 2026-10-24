using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Media;

namespace Jularr.Frontend.Framework
{
    public sealed record ImageVariant(string Url, int Width);

    // Image URLs and title/alt text must come from an authorized Backend read.
    // The Backend/image endpoint owns resizing, ETag and access control.
    public sealed class MediaImage(
        string url, string alt, int width, int height,
        IReadOnlyList<ImageVariant>? variants = null, string? sizes = null,
        bool priority = false, NoCoverKind kind = NoCoverKind.Poster, int? minViewportWidth = null) : IUiComponent
    {
        // The image where there is an address, otherwise the stand-in (NoCover): the one place a missing cover is decided.
        public static IUiComponent Or(FrontendAssets assets, string? url, string alt, int width, int height, NoCoverKind kind = NoCoverKind.Poster,
            bool priority = false, string? placeholderClass = null) =>
            string.IsNullOrWhiteSpace(url) ? new NoCover(assets, kind, placeholderClass) : new MediaImage(url, alt, width, height, priority: priority, kind: kind);

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
            // A failed load is swapped for the matching stand-in by MediaImage.entry.ts.
            if (kind == NoCoverKind.Person) image.Attribute("data-no-cover", "person");

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

            // A picture source chosen at mobile widths prevents downloading desktop-only
            // artwork even when the actual image has eager/high-priority loading.
            if (minViewportWidth is { } viewportWidth)
            {
                if (viewportWidth < 2) throw new ArgumentOutOfRangeException(nameof(minViewportWidth));
                new Picture(
                    new Source().Attribute("media", "(max-width: " + (viewportWidth - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px)")
                        .Attribute("srcset", "data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="),
                    image
                ).WriteTo(writer, encoder, texts);
                return;
            }

            image.WriteTo(writer, encoder, texts);
        }

        private sealed class Picture(params IUiComponent[] children) : HtmlElement("picture", children);
        private sealed class Source() : HtmlElement("source");

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
