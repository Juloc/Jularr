using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Reusable semantic grouping; the owning page selects its heading and contents. An icon sits before the heading on a small tile; an action (a button or a short note) sits at the heading's end.
    public sealed class UiSection(string headingKey, IReadOnlyList<IUiComponent> items, IUiComponent? icon = null, IUiComponent? action = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            IUiComponent heading = icon is null ? new H2(new Text(headingKey)) : new H2(new IconTile(icon, small: true), new Truncated(texts[headingKey])).Class("ui-section__title");
            var children = new List<IUiComponent> { action is null ? heading : new Div(heading, action).Class("ui-section__head") };
            children.AddRange(items);
            new Section([.. children]).Class("ui-section").WriteTo(writer, encoder, texts);
        }
    }
}
