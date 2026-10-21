using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;

namespace Jularr.Frontend.Framework
{
    // Real typed rows are supplied by the owning Razor PageModel after a Backend Service read.
    // Sorting/filtering/paging remain Backend-owned, not simulated by this renderer.
    public sealed class Table<TData>(IReadOnlyList<TData> rows) : IUiComponent where TData : BaseData
    {
        private static readonly ConcurrentDictionary<(PropertyInfo Property, Type Value), Delegate> Getters = new();
        private readonly List<Column> _columns = [];
        private bool _compact;

        // Rows as high as their content, for a list inside a page (tracks, versions) rather than the page's own table.
        public Table<TData> Compact()
        {
            _compact = true;
            return this;
        }

        public Table<TData> Content(string labelKey, Func<TData, IUiComponent> render)
        {
            _columns.Add(new Column(labelKey, render));
            return this;
        }

        public Table<TData> Text<TValue>(Expression<Func<TData, TValue>> selector, string labelKey)
            => Add(selector, labelKey, value => new DataValue(value));

        public Table<TData> Link<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, Func<TValue, string> href, string actionKey)
            => Add(selector, labelKey, value => new A(new Text(actionKey)).Class("button button--compact").Attribute("href", href(value)));

        public Table<TData> Resource<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, Func<TValue, string> resourceKey)
            => Add(selector, labelKey, value => new Text(resourceKey(value)));

        private Table<TData> Add<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, Func<TValue, IUiComponent> render)
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

            // Compiled once per property and value type, not per render.
            var getter = (Func<TData, TValue>)Getters.GetOrAdd((property, typeof(TValue)), _ => selector.Compile());
            _columns.Add(new Column(labelKey, item => render(getter(item))));
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (_columns.Count == 0) throw new InvalidOperationException("A typed table needs columns.");

            // Headings without rows are no table; the page says why it is empty.
            if (rows.Count == 0)
            {
                return;
            }

            var headings = _columns
                .Select(column => (IUiComponent)new Th(new Text(column.LabelKey)).Attribute("scope", "col"))
                .ToArray();

            var body = rows.Select(row => (IUiComponent)new Tr(
                // The label travels with the cell: on a phone a row becomes a card of "label value" lines (Table.scss).
                _columns.Select(column => (IUiComponent)new Cell(
                    column.Value(row)).Attribute("data-label", texts[column.LabelKey])).ToArray()
            )).ToArray();

            new Div(new TableElement(new Thead(new Tr(headings)), new Tbody(body))
                .Class("page-table")).Class(_compact ? "page-table-wrap page-table-wrap--stack page-table-wrap--compact" : "page-table-wrap page-table-wrap--stack").WriteTo(writer, encoder, texts);
        }

        private sealed class Column(string labelKey, Func<TData, IUiComponent> value)
        {
            public string LabelKey { get; } = labelKey;
            public Func<TData, IUiComponent> Value { get; } = value;
        }

        private sealed class Cell(params IUiComponent[] children) : HtmlElement("td", children);

        private sealed class DataValue(object? value) : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
                encoder.Encode(writer, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty);
        }
    }
}
