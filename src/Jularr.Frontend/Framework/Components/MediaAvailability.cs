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
        internal string GetLabel(UiText texts)
        {
            var label = texts["media.availability." + state];
            return state == MediaAvailabilityState.Partial
                ? string.Format(CultureInfo.CurrentCulture, label, availableUnits, monitoredUnits)
                : label;
        }

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
                MediaAvailabilityState.Missing => "close",
                MediaAvailabilityState.Downloading => "download",
                MediaAvailabilityState.Searching => "search",
                _ => null
            };
            if (icon is not null) symbol.Add(new Icon(icon, assets));

            // Symbol only, no interaction.
            new Span([.. symbol]).Class("media-availability media-availability--" + state.ToString().ToLowerInvariant())
                .Attribute("role", "img").Attribute("aria-label", GetLabel(texts)).WriteTo(writer, encoder, texts);
        }

        private sealed class Circle : HtmlElement
        {
            public Circle() : base("circle")
            {
                Attribute("cx", "18").Attribute("cy", "18").Attribute("r", "14").Attribute("fill", "none").Attribute("stroke", "currentColor").Attribute("stroke-width", "3");
            }
        }
    }
}
