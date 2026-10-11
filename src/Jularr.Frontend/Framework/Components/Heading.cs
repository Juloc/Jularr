using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiHeading(string titleKey, string? detailKey = null) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var content = new List<IUiComponent> { new H1(new Text(titleKey)) };
        if (detailKey is not null) content.Add(new P(new Text(detailKey)));
        new Header([.. content]).Class("page-heading").WriteTo(writer, encoder, texts);
    }
}
