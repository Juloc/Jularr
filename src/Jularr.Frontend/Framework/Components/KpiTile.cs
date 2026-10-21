using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A row of figures that sum a page up: each tile names its figure and may carry an icon. On a narrow screen the row slides sideways instead of wrapping.
    // The strip variant is one panel of divided cells whose icon sits on a tinted tile; it becomes two columns on a phone.
    public sealed class KpiTiles(params KpiTile[] tiles) : IUiComponent
    {
        private bool _strip;

        public KpiTiles Strip()
        {
            _strip = true;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(new Div([.. tiles.Select(tile => (IUiComponent)tile)]).Class("kpi-tiles__row")).Class(_strip ? "kpi-tiles kpi-tiles--strip" : "kpi-tiles").WriteTo(writer, encoder, texts);
    }

    // With a tone the icon sits on an IconTile before the figure (strip), otherwise it stands at the label's end.
    // With a foot line it is the Dashboard's card: the name with its tinted icon tile, a large figure and the line under it (in its own tone where it asks for a look).
    public sealed class KpiTile(string labelKey, IUiComponent value, IUiComponent? icon = null, UiTone? tone = null, IUiComponent? foot = null, UiTone? footTone = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (foot is not null && icon is not null)
            {
                new Div(new Div(new Span(new Text(labelKey)).Class("kpi-tile__text"), new IconTile(icon, tone ?? UiTone.Accent, small: true)).Class("kpi-tile__label"), new Div(value).Class("kpi-tile__value"),
                    new Div(foot).Class("kpi-tile__foot" + (footTone is { } warm ? " kpi-tile__foot--" + warm.ToString().ToLowerInvariant() : ""))).Class("kpi-tile kpi-tile--card").WriteTo(writer, encoder, texts);
                return;
            }

            if (tone is { } tint && icon is not null)
            {
                new Div(new IconTile(icon, tint), new Div(new Div(value).Class("kpi-tile__value"), new Div(new Text(labelKey)).Class("kpi-tile__caption")).Class("kpi-tile__figure")).Class("kpi-tile kpi-tile--tinted").WriteTo(writer, encoder, texts);
                return;
            }

            new Div(new Div(new Span(new Text(labelKey)).Class("kpi-tile__text"), icon is null ? new Span() : new Span(icon).Class("kpi-tile__icon")).Class("kpi-tile__label"),
                new Div(value).Class("kpi-tile__value")).Class("kpi-tile").WriteTo(writer, encoder, texts);
        }
    }
}
