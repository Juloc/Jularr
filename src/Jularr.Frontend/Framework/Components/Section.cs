using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Reusable semantic grouping; the owning page selects its heading and contents. An action (a button or a short note) sits at the heading's end.
    public sealed class UiSection(string headingKey, IReadOnlyList<IUiComponent> items, IUiComponent? action = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var heading = new H2(new Text(headingKey));
            var children = new List<IUiComponent> { action is null ? heading : new Div(heading, action).Class("ui-section__head") };
            children.AddRange(items);
            new Section([.. children]).Class("ui-section").WriteTo(writer, encoder, texts);
        }
    }
}
