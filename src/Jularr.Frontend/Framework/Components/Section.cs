using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Reusable semantic grouping; the owning page selects its heading and contents.
    public sealed class UiSection(string headingKey, IReadOnlyList<IUiComponent> items) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var children = new List<IUiComponent> { new H2(new Text(headingKey)) };
            children.AddRange(items);
            new Section([.. children]).Class("ui-section").WriteTo(writer, encoder, texts);
        }
    }
}
