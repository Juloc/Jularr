using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using QRCoder;

namespace Jularr.Frontend.Framework
{
    // A QR code drawn by the server, dark on light whatever the theme, so every phone camera reads it.
    public sealed class QrCode(string text, string labelKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var size = data.ModuleMatrix.Count;
            var squares = new StringBuilder();
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (data.ModuleMatrix[y][x])
                    {
                        squares.Append(CultureInfo.InvariantCulture, $"M{x} {y}h1v1h-1z");
                    }
                }
            }

            var dimension = size.ToString(CultureInfo.InvariantCulture);
            new Svg(new SvgRect().Attribute("width", dimension).Attribute("height", dimension).Attribute("fill", "#fff"),
                    new SvgPath().Attribute("d", squares.ToString()).Attribute("fill", "#000"))
                .Attribute("viewBox", "0 0 " + dimension + " " + dimension)
                .Attribute("width", "240")
                .Attribute("height", "240")
                .Attribute("shape-rendering", "crispEdges")
                .Attribute("role", "img")
                .Attribute("aria-label", texts[labelKey])
                .WriteTo(writer, encoder, texts);
        }
    }
}
