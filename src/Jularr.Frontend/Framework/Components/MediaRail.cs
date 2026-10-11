using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiMediaRail(string headingKey) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Section(new H2(new Text(headingKey)), new Div().Class("media-rail-items"))
            .Class("media-rail").WriteTo(writer, encoder, texts);
    }
}
