using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Admin.System.V1;

namespace Jularr.Frontend.Framework
{
    // The last 24 hours of the process's processor and memory use as the mockups' chart: the processor as a line over a soft area, the memory as a second line,
    // five times along the bottom and, below, what each line stands for now. The server draws it; the memory is a share of what the process may use.
    public sealed class ResourceChart(IReadOnlyList<ResourceSampleV1> samples, long availableMemoryBytes) : IUiComponent
    {
        private const int Width = 530;
        private const int Height = 165;
        private const int Left = 22;
        private const int Right = 505;
        private const int Baseline = 136;
        private const int Buckets = 48;
        private static int _charts;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (samples.Count == 0) return;

            // Evenly many points: each is the mean of the samples of its part of the window.
            var count = Math.Min(Buckets, samples.Count);
            var parts = Enumerable.Range(0, count).Select(index => samples.Skip(index * samples.Count / count).Take(Math.Max(1, (index + 1) * samples.Count / count - index * samples.Count / count)).ToList()).ToList();
            var processor = parts.Select(part => part.Average(sample => (double)sample.ProcessorPercent)).ToList();
            var memory = parts.Select(part => availableMemoryBytes > 0 ? part.Average(sample => (double)sample.MemoryBytes) / availableMemoryBytes * 100 : 0).ToList();
            var gradient = "resource-chart-fill-" + Interlocked.Increment(ref _charts).ToString(CultureInfo.InvariantCulture);

            var children = new List<IUiComponent>
            {
                new SvgDefs(new SvgGradient(new SvgStop().Class("resource-chart__stop resource-chart__stop--from").Attribute("offset", "0"), new SvgStop().Class("resource-chart__stop resource-chart__stop--to").Attribute("offset", "1"))
                    .Attribute("id", gradient).Attribute("x1", "0").Attribute("y1", "0").Attribute("x2", "0").Attribute("y2", "1"))
            };
            foreach (var y in new[] { 35, 65, 95, 125 })
            {
                children.Add(new SvgLine().Class("resource-chart__grid").Attribute("x1", "15").Attribute("x2", "518")
                    .Attribute("y1", y.ToString(CultureInfo.InvariantCulture)).Attribute("y2", y.ToString(CultureInfo.InvariantCulture)));
            }

            var line = Points(processor);
            children.Add(new SvgPolygon().Class("resource-chart__area").Attribute("fill", "url(#" + gradient + ")")
                .Attribute("points", Left.ToString(CultureInfo.InvariantCulture) + "," + Baseline.ToString(CultureInfo.InvariantCulture) + " " + line + " " + Right.ToString(CultureInfo.InvariantCulture) + "," + Baseline.ToString(CultureInfo.InvariantCulture)));
            children.Add(new Polyline().Class("resource-chart__line resource-chart__line--processor").Attribute("points", line).Attribute("fill", "none"));
            children.Add(new Polyline().Class("resource-chart__line resource-chart__line--memory").Attribute("points", Points(memory)).Attribute("fill", "none"));
            children.Add(new SvgCircle().Class("resource-chart__now").Attribute("cx", Right.ToString(CultureInfo.InvariantCulture))
                .Attribute("cy", Y(processor[^1]).ToString("0.#", CultureInfo.InvariantCulture)).Attribute("r", "4.5"));

            // Five times: the start of the window, three points between, and now.
            for (var index = 0; index < 5; index++)
            {
                var label = index == 4 ? texts["resource.now"] : samples[Math.Min(samples.Count - 1, index * samples.Count / 4)].At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                children.Add(new SvgText(new ValueText(label)).Class("resource-chart__label").Attribute("x", (17 + index * 122).ToString(CultureInfo.InvariantCulture)).Attribute("y", "156"));
            }

            var latest = samples[^1];
            var chart = new Svg([.. children]).Class("resource-chart__graph").Attribute("viewBox", "0 0 " + Width.ToString(CultureInfo.InvariantCulture) + " " + Height.ToString(CultureInfo.InvariantCulture))
                .Attribute("role", "img").Attribute("aria-label", texts["resource.chart"]);
            var memoryText = availableMemoryBytes > 0 ? ByteSize.Format(latest.MemoryBytes) + " / " + ByteSize.Format(availableMemoryBytes) : ByteSize.Format(latest.MemoryBytes);
            new Div(chart, new Div(
                new Span(new Span().Class("resource-chart__swatch resource-chart__swatch--processor"), new ValueText(texts["resource.processor"] + " · " + latest.ProcessorPercent.ToString("0.#", CultureInfo.CurrentCulture) + " %")),
                new Span(new Span().Class("resource-chart__swatch resource-chart__swatch--memory"), new ValueText(texts["resource.memory"] + " · " + memoryText))).Class("resource-chart__legend")).Class("resource-chart").WriteTo(writer, encoder, texts);
        }

        // Evenly spread from the left to the right edge; a value of 100 reaches the top line.
        private static string Points(IReadOnlyList<double> values)
        {
            var step = values.Count > 1 ? (Right - Left) / (double)(values.Count - 1) : 0;
            return string.Join(' ', values.Select((value, index) => (values.Count > 1 ? Left + index * step : Right).ToString("0.#", CultureInfo.InvariantCulture) + "," + Y(value).ToString("0.#", CultureInfo.InvariantCulture)));
        }

        private static double Y(double percent) => Baseline - Math.Clamp(percent, 0, 100) * 1.05;

        private sealed class SvgDefs(params IUiComponent[] children) : HtmlElement("defs", children);
        private sealed class SvgGradient(params IUiComponent[] children) : HtmlElement("lineargradient", children);
        private sealed class SvgStop() : HtmlElement("stop");
        private sealed class SvgLine() : HtmlElement("line");
        private sealed class SvgPolygon() : HtmlElement("polygon");
        private sealed class SvgCircle() : HtmlElement("circle");
        private sealed class SvgText(params IUiComponent[] children) : HtmlElement("text", children);
    }
}
