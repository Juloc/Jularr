using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A small line over evenly spaced values, scaled to their maximum; drawn by the server.
    public sealed class Sparkline(IReadOnlyList<double> values, string labelKey) : IUiComponent
    {
        private const int Width = 360;
        private const int Height = 64;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var maximum = values.Count > 0 ? Math.Max(values.Max(), double.Epsilon) : 1;
            var step = values.Count > 1 ? Width / (double)(values.Count - 1) : 0;
            var points = string.Join(' ', values.Select((value, index) =>
                string.Create(CultureInfo.InvariantCulture, $"{index * step:0.#},{Height - value / maximum * (Height - 2) - 1:0.#}")));
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

        private sealed class Svg(params IUiComponent[] content) : HtmlElement("svg", content);
        private sealed class Polyline() : HtmlElement("polyline");
    }
}
