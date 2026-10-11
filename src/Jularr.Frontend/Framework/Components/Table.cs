using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// Column headings are ready; rows and actions must come from real typed service data.
public sealed class UiTable(IReadOnlyList<string> headers) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var columns = headers.Select(key => (IUiComponent)new Th(new Text(key)).Attribute("scope", "col")).ToArray();
        new Div(
            new TableElement(new Thead(new Tr(columns)), new Tbody())
                .Class("page-table")
        ).Class("page-table-wrap").WriteTo(writer, encoder, texts);
    }
}
