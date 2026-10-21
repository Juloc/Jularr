using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace Jularr.Backend.Logic.Reading
{
    // A page or a region of it as an SVG picture: shapes, pictures and visible text as the PDF draws them, so a diagram or a table reads at any zoom.
    internal static class PdfSvg
    {
        private const int MaximumBytes = 3 * 1024 * 1024;
        private const int MaximumEmbeddedBytes = 2 * 1024 * 1024;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // Null when the picture would not show the page faithfully or grows too large.
        public static byte[]? Render(Page page, PdfBox region, IReadOnlyList<PdfPicture> pictures)
        {
            if (region.Width < 8 || region.Height < 8)
            {
                return null;
            }

            var svg = new StringBuilder(64 * 1024);
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(Number(region.Width)).Append(' ').Append(Number(region.Height)).Append("\" width=\"").Append(Number(region.Width))
                .Append("\" height=\"").Append(Number(region.Height)).Append("\"><rect width=\"").Append(Number(region.Width)).Append("\" height=\"").Append(Number(region.Height)).Append("\" fill=\"#fff\"/>");

            // Pictures first, then shapes, then text; the content stream's own order is not kept.
            foreach (var picture in pictures.Where(picture => picture.Box.Intersects(region)))
            {
                if (picture.Decoded is not var (bytes, mediaType, _) || bytes.Length > MaximumEmbeddedBytes)
                {
                    continue;
                }

                var box = picture.Box;
                svg.Append("<image x=\"").Append(Number(box.Left - region.Left)).Append("\" y=\"").Append(Number(region.Top - box.Top)).Append("\" width=\"").Append(Number(box.Width)).Append("\" height=\"")
                    .Append(Number(box.Height)).Append("\" preserveAspectRatio=\"none\" href=\"data:").Append(mediaType).Append(";base64,").Append(Convert.ToBase64String(bytes)).Append("\"/>");
            }

            foreach (var path in page.Paths)
            {
                if (!path.IsFilled && !path.IsStroked || path.GetBoundingRectangle() is not { } rectangle
                    || !new PdfBox(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top).Expand(path.LineWidth).Intersects(region))
                {
                    continue;
                }

                Path(svg, path, region);
                if (svg.Length > MaximumBytes)
                {
                    return null;
                }
            }

            foreach (var word in page.GetWords())
            {
                var box = new PdfBox(word.BoundingBox.Left, word.BoundingBox.Bottom, word.BoundingBox.Right, word.BoundingBox.Top);
                if (word.Letters.Count == 0 || word.Letters.All(letter => letter.RenderingMode == TextRenderingMode.Neither) || !box.Intersects(region))
                {
                    continue;
                }

                Text(svg, word, region);
                if (svg.Length > MaximumBytes)
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetBytes(svg.Append("</svg>").ToString());
        }

        private static void Path(StringBuilder svg, PdfPath path, PdfBox region)
        {
            svg.Append("<path d=\"");
            foreach (var subpath in path)
            {
                foreach (var command in subpath.Commands)
                {
                    switch (command)
                    {
                        case PdfSubpath.Move move:
                            svg.Append('M').Append(X(move.Location.X, region)).Append(' ').Append(Y(move.Location.Y, region));
                            break;
                        case PdfSubpath.Line line:
                            svg.Append('L').Append(X(line.To.X, region)).Append(' ').Append(Y(line.To.Y, region));
                            break;
                        case PdfSubpath.QuadraticBezierCurve quadratic:
                            svg.Append('Q').Append(X(quadratic.ControlPoint.X, region)).Append(' ').Append(Y(quadratic.ControlPoint.Y, region)).Append(' ')
                                .Append(X(quadratic.EndPoint.X, region)).Append(' ').Append(Y(quadratic.EndPoint.Y, region));
                            break;
                        case PdfSubpath.CubicBezierCurve curve:
                            svg.Append('C').Append(X(curve.FirstControlPoint.X, region)).Append(' ').Append(Y(curve.FirstControlPoint.Y, region)).Append(' ')
                                .Append(X(curve.SecondControlPoint.X, region)).Append(' ').Append(Y(curve.SecondControlPoint.Y, region)).Append(' ')
                                .Append(X(curve.EndPoint.X, region)).Append(' ').Append(Y(curve.EndPoint.Y, region));
                            break;
                        case PdfSubpath.Close:
                            svg.Append('Z');
                            break;
                    }
                }
            }

            svg.Append('"');
            if (path.IsFilled)
            {
                svg.Append(" fill=\"").Append(Colour(path.FillColor, "#000000")).Append('"');
                if (path.FillingRule == FillingRule.EvenOdd)
                {
                    svg.Append(" fill-rule=\"evenodd\"");
                }
            }
            else
            {
                svg.Append(" fill=\"none\"");
            }

            if (path.IsStroked)
            {
                svg.Append(" stroke=\"").Append(Colour(path.StrokeColor, "#000000")).Append("\" stroke-width=\"").Append(Number(Math.Max(0.25, (double)path.LineWidth))).Append('"');
            }

            svg.Append("/>");
        }

        private static void Text(StringBuilder svg, Word word, PdfBox region)
        {
            var first = word.Letters[0];
            var last = word.Letters[^1];
            var start = first.StartBaseLine;
            var end = last.EndBaseLine;
            var length = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI;
            var font = Font(first.FontName ?? "", first.FontDetails?.IsBold == true, first.FontDetails?.IsItalic == true);
            svg.Append("<text x=\"").Append(X(start.X, region)).Append("\" y=\"").Append(Y(start.Y, region)).Append("\" font-size=\"").Append(Number(Math.Max(1, first.PointSize))).Append("\" ").Append(font)
                .Append(" fill=\"").Append(Colour(first.FillColor, "#000000")).Append('"');
            if (length > 0.5)
            {
                svg.Append(" textLength=\"").Append(Number(length)).Append("\" lengthAdjust=\"spacingAndGlyphs\"");
            }

            if (Math.Abs(angle) > 0.5)
            {
                svg.Append(" transform=\"rotate(").Append(Number(-angle)).Append(' ').Append(X(start.X, region)).Append(' ').Append(Y(start.Y, region)).Append(")\"");
            }

            svg.Append('>').Append(Escape(word.Text)).Append("</text>");
        }

        // Family, weight and style from the font's name; the viewer's own fonts stand in for the embedded ones.
        private static string Font(string name, bool bold, bool italic)
        {
            var plain = name.Contains('+') ? name[(name.IndexOf('+') + 1)..] : name;
            var family = plain.Contains("Courier", StringComparison.OrdinalIgnoreCase) || plain.Contains("Mono", StringComparison.OrdinalIgnoreCase) || plain.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                ? "monospace"
                : new[] { "Times", "Serif", "Garamond", "Georgia", "Palatino", "Minion", "Book", "Century", "Cambria", "Caslon", "Baskerville" }.Any(hint => plain.Contains(hint, StringComparison.OrdinalIgnoreCase)) && !plain.Contains("Sans", StringComparison.OrdinalIgnoreCase)
                    ? "serif" : "sans-serif";
            bold |= plain.Contains("Bold", StringComparison.OrdinalIgnoreCase) || plain.Contains("Black", StringComparison.OrdinalIgnoreCase) || plain.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
            italic |= plain.Contains("Italic", StringComparison.OrdinalIgnoreCase) || plain.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            return "font-family=\"" + family + "\"" + (bold ? " font-weight=\"bold\"" : "") + (italic ? " font-style=\"italic\"" : "");
        }

        private static string Colour(UglyToad.PdfPig.Graphics.Colors.IColor? colour, string fallback)
        {
            if (colour is null)
            {
                return fallback;
            }

            try
            {
                var (red, green, blue) = colour.ToRGBValues();
                static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d);
                return "#" + Channel(red).ToString("x2", Invariant) + Channel(green).ToString("x2", Invariant) + Channel(blue).ToString("x2", Invariant);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
            {
                return fallback;
            }
        }

        private static string X(double x, PdfBox region) => Number(x - region.Left);

        private static string Y(double y, PdfBox region) => Number(region.Top - y);

        private static string Number(double value) => value.ToString("0.##", Invariant);

        // XML takes no control characters and no half of a surrogate pair.
        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character == '&')
                {
                    escaped.Append("&amp;");
                }
                else if (character == '<')
                {
                    escaped.Append("&lt;");
                }
                else if (character == '>')
                {
                    escaped.Append("&gt;");
                }
                else if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    escaped.Append(character).Append(text[++index]);
                }
                else if (char.IsSurrogate(character) || character < ' ' && character is not ('\t' or '\n' or '\r') || character is '￾' or '￿')
                {
                    escaped.Append('?');
                }
                else
                {
                    escaped.Append(character);
                }
            }

            return escaped.ToString();
        }
    }
}
