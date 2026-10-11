using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiMediaGrid : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
        new Div().Class("media-grid").WriteTo(writer, encoder, texts);
}
