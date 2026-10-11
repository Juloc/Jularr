using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiMediaColumns : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Div(
            new Section(new H2(new Text("details.structure"))),
            new Section(new H2(new Text("details.units"))),
            new HtmlElementAside(new H2(new Text("details.related")))
        ).Class("detail-columns").WriteTo(writer, encoder, texts);
    }

    private sealed class HtmlElementAside(params IUiComponent[] children) : HtmlElement("aside", children);
}
