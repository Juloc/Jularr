using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Framework;

public interface IUiComponent
{
    void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts);
}

// Razor boundary only. Actual HTML rendering belongs to each component.
public sealed class UiHtml(IUiComponent component, UiText texts) : IHtmlContent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder) =>
        component.WriteTo(writer, encoder, texts);
}
