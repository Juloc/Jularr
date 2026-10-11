using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// The breadcrumb is the only visible page title. Search and account actions
// are intentionally absent until their Backend/session handlers exist.
public sealed class ShellHeader(string pageTitleKey) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Header(
            new Brand(compact: true),
            new Nav(
                new Span(new Text(pageTitleKey)).Attribute("aria-current", "page")
            ).Class("app-header-location").Attribute("aria-label", texts["nav.breadcrumb"])
        ).Class("app-header").WriteTo(writer, encoder, texts);
    }
}
