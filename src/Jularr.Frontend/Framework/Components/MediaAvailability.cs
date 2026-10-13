using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Visual projection supplied by Backend.
    public enum MediaAvailabilityState
    {
        Available,
        Partial,
        Missing,
        Downloading,
        Searching
    }

    public sealed class MediaAvailability(FrontendAssets assets, MediaAvailabilityState state, int availableUnits = 0, int monitoredUnits = 0) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var percentage = state == MediaAvailabilityState.Partial
                ? monitoredUnits > 0 ? Math.Clamp(availableUnits * 100m / monitoredUnits, 0, 100) : 0
                : 100;
            var symbol = new List<IUiComponent>
            {
                new Svg(
                    new Circle().Class("media-availability__track"),
                    new Circle().Attribute("pathLength", "100").Attribute("stroke-dasharray", percentage.ToString("0.##", CultureInfo.InvariantCulture) + " 100")
                        .Attribute("transform", "rotate(-90 18 18)")
                ).Attribute("viewBox", "0 0 36 36").Attribute("aria-hidden", "true").Attribute("focusable", "false")
            };

            // Distinct symbols, stable colors.
            var icon = state switch
            {
                MediaAvailabilityState.Available => "check",
                MediaAvailabilityState.Missing => "player-close",
                MediaAvailabilityState.Downloading => "download",
                MediaAvailabilityState.Searching => "search",
                _ => null
            };
            if (icon is not null) symbol.Add(new Icon(icon, assets));
            var label = texts["media.availability." + state];
            if (state == MediaAvailabilityState.Partial)
                label = string.Format(CultureInfo.CurrentCulture, label, availableUnits, monitoredUnits);

            new Div(new IconInfo(new Span([.. symbol]).Class("media-availability__symbol"), label))
                .Class("media-availability media-availability--" + state.ToString().ToLowerInvariant()).WriteTo(writer, encoder, texts);
        }

        private sealed class Svg(params IUiComponent[] content) : HtmlElement("svg", content);
        private sealed class Circle : HtmlElement
        {
            public Circle() : base("circle")
            {
                Attribute("cx", "18").Attribute("cy", "18").Attribute("r", "14").Attribute("fill", "none").Attribute("stroke", "currentColor").Attribute("stroke-width", "3");
            }
        }
    }
}
