using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiSection(string headingKey, IReadOnlyList<IUiComponent> items) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var children = new List<IUiComponent> { new H2(new Text(headingKey)) };
        children.AddRange(items);
        new Section([.. children]).Class("page-section").WriteTo(writer, encoder, texts);
    }
}
