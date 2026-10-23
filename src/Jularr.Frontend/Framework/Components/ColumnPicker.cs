using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The list of columns a person shows, opened from the gear of a `data-work-table`; filled in from the table's headers by the client of the title page, which also keeps the choice in this browser.
    public sealed class ColumnPicker : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(new H3(new Text("work.columns")), new Ul().Class("work-columns__list")).Class("work-columns").Attribute("data-column-picker", "")
                .Attribute("data-move-label", texts["work.columns.move"]).Attribute("hidden", "hidden").WriteTo(writer, encoder, texts);
    }
}
