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
        Searching,
        Requested,
        ProfileAudio,
        OtherAudio,
        PartialOtherAudio
    }

    public sealed class MediaAvailability(FrontendAssets assets, MediaAvailabilityState state, int availableUnits = 0, int monitoredUnits = 0) : IUiComponent
    {
        private MediaAvailabilityState State { get; } = state;
        private int AvailableUnits { get; } = availableUnits;
        private int MonitoredUnits { get; } = monitoredUnits;
        private bool IsPartial => State is MediaAvailabilityState.Partial or MediaAvailabilityState.PartialOtherAudio;

        public IUiComponent Badge(string href, string? previewId = null) => new AvailabilityBadge(this, href, previewId);

        internal string GetLabel(UiText texts)
        {
            var label = texts["media.availability." + State];
            return IsPartial
                ? string.Format(CultureInfo.CurrentCulture, label, AvailableUnits, MonitoredUnits)
                : label;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) => WriteSymbol(writer, encoder, texts, false);

        private void WriteSymbol(TextWriter writer, HtmlEncoder encoder, UiText texts, bool compact)
        {
            var percentage = IsPartial
                ? MonitoredUnits > 0 ? Math.Clamp(AvailableUnits * 100m / MonitoredUnits, 0, 100) : 0
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
            var icon = State switch
            {
                MediaAvailabilityState.Available or MediaAvailabilityState.ProfileAudio => "check",
                MediaAvailabilityState.OtherAudio or MediaAvailabilityState.PartialOtherAudio => "player-volume",
                MediaAvailabilityState.Missing => "close",
                MediaAvailabilityState.Downloading => "download",
                MediaAvailabilityState.Searching => "search",
                MediaAvailabilityState.Requested => "requests",
                _ => null
            };
            if (icon is not null) symbol.Add(new Icon(icon, assets));

            // Symbol only, no interaction.
            new Span([.. symbol]).Class("media-availability media-availability--" + State.ToString().ToLowerInvariant() + (compact ? " media-availability--compact" : ""))
                .Attribute("role", "img").Attribute("aria-label", GetLabel(texts)).WriteTo(writer, encoder, texts);
        }

        // Dot and expanded status capsule.
        private sealed class AvailabilityBadge(MediaAvailability symbol, string href, string? previewId) : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
            {
                if (symbol.State == MediaAvailabilityState.Missing) return;
                var label = symbol.GetLabel(texts);
                var badgeLabel = texts["media.availability.badge." + symbol.State];
                if (symbol.IsPartial) badgeLabel = string.Format(CultureInfo.CurrentCulture, badgeLabel, symbol.AvailableUnits, symbol.MonitoredUnits);
                var link = new A(new Span(new CompactSymbol(symbol), new Span(new ValueText(badgeLabel)).Class("media-availability-badge__label"))
                    .Class("media-availability-badge__body"), new Span(new ValueText(label)).Class("media-availability-badge__hint").Attribute("aria-hidden", "true"))
                    .Class("media-availability-badge media-availability-badge--" + symbol.State.ToString().ToLowerInvariant())
                    .Attribute("href", href).Attribute("aria-label", label);
                if (previewId is not null) link.Attribute("data-preview-open", previewId).Attribute("aria-haspopup", "dialog");
                link.WriteTo(writer, encoder, texts);
            }
        }

        private sealed class CompactSymbol(MediaAvailability symbol) : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) => symbol.WriteSymbol(writer, encoder, texts, true);
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
