using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A modal panel from the right edge (full screen on phones) with a heading, a scrolling body and a footer.
    // A button with data-drawer-open="id" opens it; those buttons show once the script has run, since the panel needs it.
    // With a form address the body and footer are one GET form, whose submit button reloads the page with the choices.
    public sealed class Drawer(FrontendAssets assets, string id, string titleKey, IReadOnlyList<IUiComponent> body, IReadOnlyList<IUiComponent> footer, string? formAddress = null) : IUiComponent
    {
        public static HtmlElement Opener(HtmlElement button, string id) =>
            button.Attribute("data-drawer-open", id).Attribute("aria-haspopup", "dialog").Attribute("aria-controls", id);

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var heading = new Div(new H2(new Text(titleKey)),
                new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("data-drawer-close", "")).Class("drawer__heading");
            var content = new Div([.. body]).Class("drawer__body");
            var actions = new Div([.. footer]).Class("drawer__footer");
            IUiComponent inner = formAddress is null
                ? new Div(content, actions).Class("drawer__content")
                : new GetForm(content, actions).Class("drawer__content").Attribute("action", formAddress).Attribute("method", "get");
            new Dialog(heading, inner).Class("drawer").Attribute("id", id).Attribute("aria-label", texts[titleKey]).WriteTo(writer, encoder, texts);
        }

        private sealed class Dialog(params IUiComponent[] content) : HtmlElement("dialog", content);

        private sealed class GetForm(params IUiComponent[] content) : HtmlElement("form", content);
    }
}
