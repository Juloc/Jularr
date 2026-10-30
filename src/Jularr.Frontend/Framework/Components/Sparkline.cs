using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A small line over evenly spaced values, scaled to a fixed maximum or else to their own; drawn by the server. A line the live stream keeps current names the text that carries its points.
    public sealed class Sparkline(IReadOnlyList<double> values, string labelKey, double? maximum = null, int height = 64, string? livePoints = null) : IUiComponent
    {
        private const int Width = 360;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var line = new Polyline().Attribute("points", Points(values, maximum, height)).Attribute("fill", "none").Attribute("stroke", "currentColor").Attribute("stroke-width", "1.5")
                .Attribute("vector-effect", "non-scaling-stroke");
            if (livePoints is not null) line.Attribute("data-live-points", livePoints);
            new Svg(line)
                .Class("sparkline")
                .Attribute("viewBox", "0 0 " + Width.ToString(CultureInfo.InvariantCulture) + " " + height.ToString(CultureInfo.InvariantCulture))
                .Attribute("preserveAspectRatio", "none")
                .Attribute("width", "100%")
                .Attribute("height", height.ToString(CultureInfo.InvariantCulture))
                .Attribute("role", "img")
                .Attribute("aria-label", texts[labelKey])
                .WriteTo(writer, encoder, texts);
        }

        // The polyline's points for the values; a single sample is a flat line across.
        public static string Points(IReadOnlyList<double> values, double? maximum = null, int height = 64)
        {
            IReadOnlyList<double> line = values.Count == 1 ? [values[0], values[0]] : values;
            var top = maximum ?? (line.Count > 0 ? Math.Max(line.Max(), double.Epsilon) : 1);
            var step = line.Count > 1 ? Width / (double)(line.Count - 1) : 0;
            return string.Join(' ', line.Select((value, index) =>
                string.Create(CultureInfo.InvariantCulture, $"{index * step:0.#},{height - Math.Clamp(value / top, 0, 1) * (height - 2) - 1:0.#}")));
        }
    }
}
