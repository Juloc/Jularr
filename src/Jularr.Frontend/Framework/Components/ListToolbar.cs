using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The one row above an admin list: the filter chips (NavigationTabs with the `filters` modifier and counts) on the left, the list's own tools (a filter field, a count, a filter button) on the right.
    // It stays one row at every width; on a phone the chips take the first line and the tools the second.
    public sealed class ListToolbar(NavigationTabs chips, params IUiComponent[] tools) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(chips, new Div([.. tools]).Class("list-toolbar__tools")).Class("list-toolbar").WriteTo(writer, encoder, texts);

        // The count of rows that a page filter leaves, kept current by the filter field (data-list-count, with its text as the format).
        public static IUiComponent Count(string text, string format) =>
            new Span(new ValueText(text)).Class("list-toolbar__count").Attribute("data-list-count", "").Attribute("data-format", format);
    }
}
