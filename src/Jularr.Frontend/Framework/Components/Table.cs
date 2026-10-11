using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;

namespace Jularr.Frontend.Framework;

// Prepared headings until a typed authorized list read has been connected.
public sealed class UiTable(IReadOnlyList<string> headers) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var columns = headers.Select(key => (IUiComponent)new Th(new Text(key)).Attribute("scope", "col")).ToArray();
        new Div(new TableElement(new Thead(new Tr(columns)), new Tbody()).Class("page-table"))
            .Class("page-table-wrap").WriteTo(writer, encoder, texts);
    }
}

// Real typed rows are supplied by the owning Razor PageModel after a Backend Service read.
// Sorting/filtering/paging remain Backend-owned, not simulated by this renderer.
public sealed class Table<TData>(IReadOnlyList<TData> rows) : IUiComponent where TData : BaseData
{
    private readonly List<Column> _columns = [];

    public Table<TData> Text<TValue>(Expression<Func<TData, TValue>> selector, string labelKey)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } cast
            ? cast.Operand
            : selector.Body;

        if (body is not MemberExpression { Member: PropertyInfo property }
            || property.DeclaringType is null
            || !property.DeclaringType.IsAssignableFrom(typeof(TData)))
        {
            throw new ArgumentException("A table column must select a data property.", nameof(selector));
        }

        var getter = selector.Compile();
        _columns.Add(new Column(labelKey, item => getter(item)));
        return this;
    }

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        if (_columns.Count == 0) throw new InvalidOperationException("A typed table needs columns.");

        var headings = _columns
            .Select(column => (IUiComponent)new Th(new Text(column.LabelKey)).Attribute("scope", "col"))
            .ToArray();

        var body = rows.Select(row => (IUiComponent)new Tr(
            _columns.Select(column => (IUiComponent)new Cell(
                new DataValue(column.Value(row)))).ToArray()
        )).ToArray();

        new Div(new TableElement(new Thead(new Tr(headings)), new Tbody(body))
            .Class("page-table")).Class("page-table-wrap").WriteTo(writer, encoder, texts);
    }

    private sealed record Column(string LabelKey, Func<TData, object?> Value);

    private sealed class Cell(params IUiComponent[] children) : HtmlElement("td", children);

    private sealed class DataValue(object? value) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            encoder.Encode(writer, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty);
    }
}
