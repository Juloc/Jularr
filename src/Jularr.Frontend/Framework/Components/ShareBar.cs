using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The colour of one part of a bar; Free is the empty track.
    public enum ShareTone
    {
        First,
        Second,
        Third,
        Fourth,
        Other,
        Free
    }

    // One part of the whole. A part with a legend key is named below the bar; its fraction is of the whole (0 to 1).
    public sealed record ShareSegment(ShareTone Tone, double Fraction, string? LegendKey = null);

    // One bar shows how a whole divides, drawn by the server as one SVG (no inline styles): the parts side by side, the legend below.
    public sealed class ShareBar(IReadOnlyList<ShareSegment> segments, string labelKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var parts = new List<IUiComponent>();
            double start = 0;
            foreach (var segment in segments.Where(segment => segment.Fraction > 0))
            {
                var width = Math.Min(segment.Fraction * 100, 100 - start);
                parts.Add(new SvgRect().Class("share-bar__part share-bar__part--" + segment.Tone.ToString().ToLowerInvariant()).Attribute("x", start.ToString("0.###", CultureInfo.InvariantCulture))
                    .Attribute("y", "0").Attribute("width", width.ToString("0.###", CultureInfo.InvariantCulture)).Attribute("height", "1"));
                start += width;
            }

            var bar = new Div(new Svg([.. parts]).Class("share-bar__graph").Attribute("viewBox", "0 0 100 1").Attribute("preserveAspectRatio", "none").Attribute("role", "img")
                .Attribute("aria-label", texts[labelKey])).Class("share-bar__track");
            var legend = segments.Where(segment => segment.LegendKey is not null && segment.Fraction > 0).Select(segment => (IUiComponent)new Span(
                new Span().Class("share-bar__swatch share-bar__swatch--" + segment.Tone.ToString().ToLowerInvariant()), new Text(segment.LegendKey!))).ToArray();
            new Div(bar, legend.Length == 0 ? new Span() : new Div(legend).Class("share-bar__legend")).Class("share-bar").WriteTo(writer, encoder, texts);
        }
    }
}
