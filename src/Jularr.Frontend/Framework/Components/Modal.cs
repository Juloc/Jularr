using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The one structure of a centred dialog: a title row with the X at its right, the body, and the footer. The footer is the actions of the
    // form in the body (Form.Cancel and its primary action), so the primary action stays at the bottom and every dialog saves or cancels on its own.
    // A dialog the address opened (an item to edit) names the address of its list, which replaces it in the history when the dialog closes.
    // An element with data-dialog-open="<id>" opens it; Modal.entry.ts closes it with the X, Cancel, Escape or a click on the backdrop.
    public sealed class Modal(FrontendAssets assets, string id, string titleKey, IReadOnlyList<IUiComponent> body, bool open = false, string? returnTo = null) : IUiComponent
    {
        public static HtmlElement Opener(HtmlElement button, string id) => button.Attribute("data-dialog-open", id).Attribute("aria-haspopup", "dialog").Attribute("aria-controls", id);

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var dialog = new Dialog(
                new Div(new H2(new Text(titleKey)).Attribute("id", id + "-title"),
                    new Button(new Icon("close", assets, 18)).IconOnly(texts["common.close"]).Attribute("data-dialog-close", "")).Class("modal__head"),
                new Div([.. body]).Class("modal__body"))
                .Class("modal").Attribute("id", id).Attribute("aria-labelledby", id + "-title").Attribute("data-open", open ? "true" : "false");
            (returnTo is null ? dialog : dialog.Attribute("data-return", returnTo)).WriteTo(writer, encoder, texts);
        }

        private sealed class Dialog(params IUiComponent[] children) : HtmlElement("dialog", children);
    }
}
