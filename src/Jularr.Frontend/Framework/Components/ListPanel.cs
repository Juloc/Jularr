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
    // A column with a key can be shown, hidden and moved by the person (the column picker): its width is the grid track it takes, Fixed ones are not offered, Hidden ones start out of view.
    // A head replaces the label (a sort link, a select-all box).
    public sealed record ListColumn(string LabelKey, ListRole Role = ListRole.Data, int HideLevel = 0, string? Key = null, string? Width = null, bool Fixed = false, bool Hidden = false, IUiComponent? Head = null);

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

    // A cover as the leading tile of a row (the poster of a title); without one a quiet placeholder stands in.
    public sealed class ListPoster : HtmlElement
    {
        // A large cover for the rows that carry more than a name (an overview).
        public ListPoster(IUiComponent? image, bool large = false) : base("span", image ?? new Span())
        {
            Class(large ? "list-poster list-poster--large" : "list-poster");
            Attribute("aria-hidden", "true");
        }
    }

    // The name cell: a leading visual, a title (the control that opens the row's details, or plain text), one line under it, more lines of facts (an overview) and an optional note line (a failure) in the danger colour.
    public sealed class ListIdentity : HtmlElement
    {
        // The lead is an icon tile or a poster; a row that can be chosen puts its box first (ListIdentity.Select).
        public ListIdentity(IUiComponent lead, IUiComponent title, IUiComponent subline, IUiComponent? note = null, IUiComponent? select = null, IReadOnlyList<IUiComponent>? more = null)
            : base("div", [.. select is null ? [] : new[] { select }, lead, new Div([new Span(title).Class("list-identity__name"), new Span(subline).Class("list-identity__sub"), .. (more ?? []).Select(line => (IUiComponent)new Span(line).Class("list-identity__sub")), .. note is null ? [] : new IUiComponent[] { new Span(note).Class("list-identity__note") }]).Class("list-identity__text")])
        {
            Class("list-identity");
        }

        // The box of a row that can be chosen: it counts in the dock's bulk zone and belongs to the form named by formId, which the bulk buttons carry.
        public static IUiComponent Select(string name, string value, string formId, string labelKey) =>
            new Input().Attribute("type", "checkbox").Attribute("name", name).Attribute("value", value).Attribute("form", formId).Attribute("data-dock-select", "").Class("list-select").TextAttribute("aria-label", labelKey);

        // The head of the name column of a list whose rows can be chosen: the box that chooses every row of the page (the dock counts them), before the head's label or sort link.
        public static IUiComponent SelectAllHead(IUiComponent label, string labelKey) =>
            new Div(new Input().Attribute("type", "checkbox").Class("list-select").Attribute("data-dock-select-all", "").TextAttribute("aria-label", labelKey), label).Class("list-head-title");

        // The title as the button that opens a drawer.
        public static HtmlElement TitleButton(IUiComponent label, string drawerId) =>
            Drawer.Opener(new Button(label).Class("list-identity__title"), drawerId);
    }

    // The admin list kit: a panel of rows laid out on a grid, with a head, that becomes one card per row on a narrow container. Pages give the columns, the rows and (in their stylesheet) the grid widths.
    // Filters, counts and details drawers belong beside it: NavigationTabs with counts, SearchBox, KpiTiles, Drawer.
    public sealed class ListPanel(string ariaLabelKey, IReadOnlyList<ListColumn> columns, IReadOnlyList<ListRow> rows, IUiComponent? empty = null) : IUiComponent
    {
        private string _modifier = "";
        private string? _pickerKind;

        // The page's own class, which sets --list-columns (and -medium, -narrow) for its columns.
        public ListPanel Modifier(string name)
        {
            _modifier = " " + name;
            return this;
        }

        // The person shows, hides and moves the keyed columns (a gear opens the picker, the choice stays in this browser under the kind); the script then sets the grid from the columns' widths.
        public ListPanel Pickable(string kind)
        {
            _pickerKind = kind;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var head = new Div([.. columns.Select(column =>
            {
                var cell = new Div(column.Head ?? new Text(column.LabelKey)).Class(CellClass(column)).Attribute("role", "columnheader").TextAttribute("title", column.LabelKey);
                return (IUiComponent)Keyed(cell, column, texts);
            })]).Class("list-head").Attribute("role", "row");
            var body = new List<IUiComponent>();
            foreach (var row in rows)
            {
                var cells = new List<IUiComponent>();
                for (var index = 0; index < columns.Count && index < row.Cells.Count; index++)
                {
                    var cell = new Div(row.Cells[index]).Class(CellClass(columns[index])).Attribute("role", "cell");
                    if (columns[index].Hidden) cell.Attribute("hidden", "hidden");
                    cells.Add(columns[index].Key is { } key ? cell.Attribute("data-col", key) : cell);
                }

                if (row.Caption is { } caption) cells.Add(new Div([.. caption]).Class("list-row__caption"));
                var element = new Div([.. cells]).Class("list-row" + (row.IsChild ? " list-row--child" : "") + (row.Tint is { } tone ? " list-row--" + tone.ToString().ToLowerInvariant() : "")).Attribute("role", "row");
                body.Add(row.FilterText is { } text ? element.Attribute("data-list-text", text.ToLowerInvariant()) : element);
            }

            var parts = new List<IUiComponent> { head, new Div([.. body]).Class("list-body").Attribute("role", "rowgroup") };
            if (empty is not null) parts.Add(rows.Count == 0 ? new Div(empty).Class("list-empty") : new Div(empty).Class("list-empty").Attribute("hidden", "hidden"));
            var panel = new Div([.. parts]).Class("list-panel" + _modifier).Attribute("role", "table").Attribute("aria-label", texts[ariaLabelKey]);
            new Div(_pickerKind is null ? panel : panel.Attribute("data-work-table", _pickerKind)).Class("list").WriteTo(writer, encoder, texts);
        }

        // What the column picker reads from a head cell: the key, the name it shows, the grid track, when the column leaves and whether it starts hidden or fixed.
        private static HtmlElement Keyed(HtmlElement cell, ListColumn column, UiText texts)
        {
            if (column.Key is not { } key) return cell;
            cell.Attribute("data-col", key).Attribute("data-label", texts[column.LabelKey]).Attribute("data-hidden", column.Hidden ? "true" : "false")
                .Attribute("data-hide", column.HideLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (column.Width is { } width) cell.Attribute("data-width", width);
            if (column.Hidden) cell.Attribute("hidden", "hidden");
            return column.Fixed ? cell.Attribute("data-fixed", "true") : cell;
        }

        // A head that sorts: a link to the list in that order; the current order shows an arrow (direction "ascending" or "descending").
        public static IUiComponent SortLink(string labelKey, string href, string? direction = null) =>
            new A(new Text(labelKey)).Class(direction is null ? "list-sort" : "list-sort list-sort--" + direction).Attribute("href", href);

        // One fact of a caption: its label, a colon and its value in the text colour, with an optional icon first.
        public static IUiComponent Fact(string labelKey, IUiComponent value, IUiComponent? icon = null) =>
            new Span([.. icon is null ? [] : new[] { icon }, new Text(labelKey), new ValueText(": "), new Span(value).Class("list-row__fact")]);

        private static string CellClass(ListColumn column) =>
            "list-cell list-cell--" + column.Role.ToString().ToLowerInvariant() + (column.HideLevel > 0 ? " list-cell--hide-" + column.HideLevel : "");
    }
}
