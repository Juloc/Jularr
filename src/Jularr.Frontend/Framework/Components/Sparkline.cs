using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A small line over evenly spaced values, scaled to a fixed maximum or else to their own; drawn by the server.
    public sealed class Sparkline(IReadOnlyList<double> values, string labelKey, double? maximum = null) : IUiComponent
    {
        private const int Width = 360;
        private const int Height = 64;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // A single sample is a flat line across.
            IReadOnlyList<double> line = values.Count == 1 ? [values[0], values[0]] : values;
            var top = maximum ?? (line.Count > 0 ? Math.Max(line.Max(), double.Epsilon) : 1);
            var step = line.Count > 1 ? Width / (double)(line.Count - 1) : 0;
            var points = string.Join(' ', line.Select((value, index) =>
                string.Create(CultureInfo.InvariantCulture, $"{index * step:0.#},{Height - Math.Clamp(value / top, 0, 1) * (Height - 2) - 1:0.#}")));
            new Svg(new Polyline().Attribute("points", points).Attribute("fill", "none").Attribute("stroke", "currentColor").Attribute("stroke-width", "1.5")
                    .Attribute("vector-effect", "non-scaling-stroke"))
                .Class("sparkline")
                .Attribute("viewBox", "0 0 " + Width.ToString(CultureInfo.InvariantCulture) + " " + Height.ToString(CultureInfo.InvariantCulture))
                .Attribute("preserveAspectRatio", "none")
                .Attribute("width", "100%")
                .Attribute("height", Height.ToString(CultureInfo.InvariantCulture))
                .Attribute("role", "img")
                .Attribute("aria-label", texts[labelKey])
                .WriteTo(writer, encoder, texts);
        }
    }
}
