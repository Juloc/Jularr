using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // What a column is for: Main names the row (and is the card's title), Status sits beside it on a card, Data is dropped from a card (its row's caption says it), Actions sit under it.
    public enum ListRole
    {
        Main,
        Status,
        Data,
        Actions
    }

    // HideLevel 1 leaves the column out below the medium width, 2 below the narrow width; the page's stylesheet sets the grid for each width.
    public sealed record ListColumn(string LabelKey, ListRole Role = ListRole.Data, int HideLevel = 0);

    // One row of cells, one per column in the same order. The caption is the phone card's line of facts; the filter text is what a page filter searches.
    public sealed class ListRow(IReadOnlyList<IUiComponent> cells, IReadOnlyList<IUiComponent>? caption = null, string? filterText = null)
    {
        public IReadOnlyList<IUiComponent> Cells => cells;
        public IReadOnlyList<IUiComponent>? Caption => caption;
        public string? FilterText => filterText;
        public bool IsChild { get; private set; }
        public UiTone? Tint { get; private set; }

        // A row that belongs to the one above: indented, with the smaller icon its page gives it.
        public ListRow Child()
        {
            IsChild = true;
            return this;
        }

        // A quiet wash of a status tint under the whole row.
        public ListRow Tinted(UiTone tone)
        {
            Tint = tone;
            return this;
        }
    }

    // The name cell: an icon tile, a title (the control that opens the row's details, or plain text) and one line under it and an optional note line (a failure) in the danger colour.
    public sealed class ListIdentity : HtmlElement
    {
        public ListIdentity(IconTile tile, IUiComponent title, IUiComponent subline, IUiComponent? note = null) : base("div", tile, new Div([new Span(title).Class("list-identity__name"), new Span(subline).Class("list-identity__sub"), .. note is null ? [] : new IUiComponent[] { new Span(note).Class("list-identity__note") }]).Class("list-identity__text"))
        {
            Class("list-identity");
        }

        // The title as the button that opens a drawer.
        public static HtmlElement TitleButton(IUiComponent label, string drawerId) =>
            Drawer.Opener(new Button(label).Class("list-identity__title"), drawerId);
    }

    // The admin list kit: a panel of rows laid out on a grid, with a head, that becomes one card per row on a narrow container. Pages give the columns, the rows and (in their stylesheet) the grid widths.
    // Filters, counts and details drawers belong beside it: NavigationTabs with counts, SearchBox, KpiTiles, Drawer.
    public sealed class ListPanel(string ariaLabelKey, IReadOnlyList<ListColumn> columns, IReadOnlyList<ListRow> rows, IUiComponent? empty = null) : IUiComponent
    {
        private string _modifier = "";

        // The page's own class, which sets --list-columns (and -medium, -narrow) for its columns.
        public ListPanel Modifier(string name)
        {
            _modifier = " " + name;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var head = new Div([.. columns.Select(column => (IUiComponent)new Div(new Text(column.LabelKey)).Class(CellClass(column)).Attribute("role", "columnheader"))])
                .Class("list-head").Attribute("role", "row");
            var body = new List<IUiComponent>();
            foreach (var row in rows)
            {
                var cells = new List<IUiComponent>();
                for (var index = 0; index < columns.Count && index < row.Cells.Count; index++)
                {
                    cells.Add(new Div(row.Cells[index]).Class(CellClass(columns[index])).Attribute("role", "cell"));
                }

                if (row.Caption is { } caption) cells.Add(new Div([.. caption]).Class("list-row__caption"));
                var element = new Div([.. cells]).Class("list-row" + (row.IsChild ? " list-row--child" : "") + (row.Tint is { } tone ? " list-row--" + tone.ToString().ToLowerInvariant() : "")).Attribute("role", "row");
                body.Add(row.FilterText is { } text ? element.Attribute("data-list-text", text.ToLowerInvariant()) : element);
            }

            var parts = new List<IUiComponent> { head, new Div([.. body]).Class("list-body").Attribute("role", "rowgroup") };
            if (empty is not null) parts.Add(rows.Count == 0 ? new Div(empty).Class("list-empty") : new Div(empty).Class("list-empty").Attribute("hidden", "hidden"));
            new Div(new Div([.. parts]).Class("list-panel" + _modifier).Attribute("role", "table").Attribute("aria-label", texts[ariaLabelKey])).Class("list").WriteTo(writer, encoder, texts);
        }

        // One fact of a caption: its label, a colon and its value in the text colour, with an optional icon first.
        public static IUiComponent Fact(string labelKey, IUiComponent value, IUiComponent? icon = null) =>
            new Span([.. icon is null ? [] : new[] { icon }, new Text(labelKey), new ValueText(": "), new Span(value).Class("list-row__fact")]);

        private static string CellClass(ListColumn column) =>
            "list-cell list-cell--" + column.Role.ToString().ToLowerInvariant() + (column.HideLevel > 0 ? " list-cell--hide-" + column.HideLevel : "");
    }
}
