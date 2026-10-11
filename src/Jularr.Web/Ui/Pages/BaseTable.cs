namespace Jularr.Web.Ui.Pages;

public enum UiTableMode
{
    Cards,
    Rows
}

public sealed record UiTableRow(string Anchor, IReadOnlyList<string?> Values, object Item);

public sealed record UiTableCard(string Anchor, string Title, string? Subtitle, string? CoverUrl, object Item);

public sealed record UiTableView(
    string Label,
    UiTableMode Mode,
    IReadOnlyList<string> Headers,
    IReadOnlyList<UiTableRow> Rows,
    IReadOnlyList<UiTableCard> Cards,
    string? DetailsPartial,
    string? DetailsHeader);

public abstract class BaseTable<TItem>(IReadOnlyList<TItem> items) : IUiPageComponent
{
    public string PartialName => "_UiDataList";

    public object ViewModel
    {
        get
        {
            var table = new Table<TItem>();
            Define(table);
            return table.Build(items);
        }
    }

    protected abstract void Define(Table<TItem> table);
}

public sealed class Table<TItem>
{
    private Func<TItem, string> anchor = _ => string.Empty;
    public string Label { get; set; } = string.Empty;
    public UiTableMode Mode { get; set; }
    public TableRows<TItem> Rows { get; } = new();
    public TableCards<TItem> Cards { get; } = new();

    public void Anchor(Func<TItem, string> selector) => anchor = selector;

    internal UiTableView Build(IReadOnlyList<TItem> items) => new(
        Label,
        Mode,
        Rows.Columns.Select(column => column.Label).ToArray(),
        items.Select(item => new UiTableRow(
            anchor(item),
            Rows.Columns.Select(column => column.Value(item)).ToArray(),
            item!)).ToArray(),
        items.Select(item => new UiTableCard(
            anchor(item),
            Cards.TitleValue?.Invoke(item) ?? string.Empty,
            Cards.SubtitleValue?.Invoke(item),
            Cards.CoverValue?.Invoke(item),
            item!)).ToArray(),
        Cards.DetailsPartial,
        Cards.DetailsHeader);
}

public sealed class TableRows<TItem>
{
    internal List<(string Label, Func<TItem, string?> Value)> Columns { get; } = [];

    public void Text(string label, Func<TItem, string?> value) => Columns.Add((label, value));
}

public sealed class TableCards<TItem>
{
    internal Func<TItem, string>? TitleValue { get; private set; }
    internal Func<TItem, string?>? SubtitleValue { get; private set; }
    internal Func<TItem, string?>? CoverValue { get; private set; }
    internal string? DetailsPartial { get; private set; }
    internal string? DetailsHeader { get; private set; }

    public void Title(Func<TItem, string> value) => TitleValue = value;

    public void Subtitle(Func<TItem, string?> value) => SubtitleValue = value;

    public void Cover(Func<TItem, string?> value) => CoverValue = value;

    public void Details(string partialName, string header)
    {
        DetailsPartial = partialName;
        DetailsHeader = header;
    }
}
