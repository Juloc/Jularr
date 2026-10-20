using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One page of a drawer: its tab (with an icon), its heading, a scrolling body and a footer.
    // With a form address the body and footer are one GET form, whose submit button reloads the page with the choices.
    public sealed record DrawerPane(string Id, string TabKey, string TitleKey, string Icon, IReadOnlyList<IUiComponent> Body, IReadOnlyList<IUiComponent> Footer, string? FormAddress = null);

    // A modal panel from the right edge (full screen on phones). Several panes show a tab row that switches between them.
    // A button with data-drawer-open="id" (and data-drawer-pane="pane") opens it; those buttons show once the script has run, since the panel needs it.
    public sealed class Drawer(FrontendAssets assets, string id, IReadOnlyList<DrawerPane> panes) : IUiComponent
    {
        public static HtmlElement Opener(HtmlElement button, string id, string? pane = null)
        {
            button.Attribute("data-drawer-open", id).Attribute("aria-haspopup", "dialog").Attribute("aria-controls", id);
            return pane is null ? button : button.Attribute("data-drawer-pane", pane);
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var heading = new Div(new H2(new Text(panes[0].TitleKey)).Attribute("id", id + "-title"),
                new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Class("button button--icon drawer__close").Attribute("data-drawer-close", "")).Class("drawer__heading");
            var parts = new List<IUiComponent> { heading };

            if (panes.Count > 1)
            {
                parts.Add(new Div([.. panes.Select((pane, index) =>
                {
                    var tab = new Button(new Icon(pane.Icon, assets, 16), new Text(pane.TabKey)).Class(index == 0 ? "drawer__tab drawer__tab--selected" : "drawer__tab")
                        .Attribute("role", "tab").Attribute("id", id + "-tab-" + pane.Id).Attribute("aria-controls", id + "-" + pane.Id)
                        .Attribute("aria-selected", index == 0 ? "true" : "false").Attribute("data-drawer-tab", pane.Id).Attribute("data-drawer-title", texts[pane.TitleKey]);
                    return (IUiComponent)tab;
                })]).Class("drawer__tabs").Attribute("role", "tablist"));
            }

            for (var index = 0; index < panes.Count; index++)
            {
                var pane = panes[index];
                var content = new Div([.. pane.Body]).Class("drawer__body");
                var actions = new Div([.. pane.Footer]).Class("drawer__footer");
                HtmlElement inner = pane.FormAddress is null
                    ? new Div(content, actions).Class("drawer__content")
                    : new GetForm(content, actions).Class("drawer__content").Attribute("action", pane.FormAddress).Attribute("method", "get");
                var element = new Div(inner).Class("drawer__pane").Attribute("id", id + "-" + pane.Id).Attribute("data-drawer-pane", pane.Id);
                if (panes.Count > 1) element.Attribute("role", "tabpanel").Attribute("aria-labelledby", id + "-tab-" + pane.Id);
                if (index > 0) element.Attribute("hidden", "hidden");
                parts.Add(element);
            }

            new Dialog([.. parts]).Class("drawer").Attribute("id", id).Attribute("aria-labelledby", id + "-title").WriteTo(writer, encoder, texts);
        }

        private sealed class Dialog(params IUiComponent[] content) : HtmlElement("dialog", content);

        private sealed class GetForm(params IUiComponent[] content) : HtmlElement("form", content);
    }
}
