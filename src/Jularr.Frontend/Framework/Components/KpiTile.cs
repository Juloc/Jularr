using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A row of figures that sum a page up: each tile names its figure and may carry an icon. On a narrow screen the row slides sideways instead of wrapping.
    public sealed class KpiTiles(params KpiTile[] tiles) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div([.. tiles.Select(tile => (IUiComponent)tile)]).Class("kpi-tiles").WriteTo(writer, encoder, texts);
    }

    public sealed class KpiTile(string labelKey, IUiComponent value, IUiComponent? icon = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(new Div(new Span(new Text(labelKey)).Class("kpi-tile__text"), icon is null ? new Span() : new Span(icon).Class("kpi-tile__icon")).Class("kpi-tile__label"),
                new Div(value).Class("kpi-tile__value")).Class("kpi-tile").WriteTo(writer, encoder, texts);
    }
}
