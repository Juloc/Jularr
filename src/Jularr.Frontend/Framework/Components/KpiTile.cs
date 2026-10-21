using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The tint of a tile's icon and foot line: lavender is the default, green says fine, amber asks for a look, blue is information.
    public enum KpiTone
    {
        Accent,
        Good,
        Warn,
        Info
    }

    // A row of figures that sum a page up. Four across where there is room, two on a narrow screen.
    public sealed class KpiTiles(params KpiTile[] tiles) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (tiles.Length == 0) return;

            new Div([.. tiles.Select(tile => (IUiComponent)tile)]).Class("kpi-tiles").WriteTo(writer, encoder, texts);
        }
    }

    // One figure: its name with an icon on a tinted tile, the figure itself, and one line below it. The value and the foot may be live elements.
    public sealed class KpiTile(string labelKey, IUiComponent value, IUiComponent icon, KpiTone tone = KpiTone.Accent, IUiComponent? foot = null, KpiTone? footTone = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var children = new List<IUiComponent>
            {
                new Div(new Span(new Text(labelKey)).Class("kpi-tile__text"), new Span(icon).Class("kpi-tile__icon kpi-tile__icon--" + Name(tone))).Class("kpi-tile__top"),
                new Div(value).Class("kpi-tile__value")
            };
            if (foot is not null) children.Add(new Div(foot).Class("kpi-tile__foot" + (footTone is { } tint ? " kpi-tile__foot--" + Name(tint) : "")));

            new Div([.. children]).Class("kpi-tile").WriteTo(writer, encoder, texts);
        }

        private static string Name(KpiTone tone) => tone.ToString().ToLowerInvariant();
    }
}
