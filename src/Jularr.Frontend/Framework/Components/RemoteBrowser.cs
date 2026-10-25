using System.Globalization;
using Jularr.Backend.Data.Admin.Browser.V1;

namespace Jularr.Frontend.Framework
{
    // A picture of the source browser; only explicit input events leave this component.
    public sealed class RemoteBrowser(BrowserViewV1 view, string? csrf) : IUiComponent
    {
        public void WriteTo(TextWriter writer, System.Text.Encodings.Web.HtmlEncoder encoder, UiText texts)
        {
            var address = "/api/v1/admin/browser/assistance/" + view.LeaseId.ToString("D");
            var content = new Div(
                new P(new Text("admin.browser.manualHint")),
                new BrowserImage().Class("remote-browser__image").Attribute("src", address + "/frame").Attribute("alt", texts["admin.browser.manualImage"])
                    .Attribute("tabindex", "0").Attribute("draggable", "false").Attribute("data-browser-image", ""),
                new P(new Text("admin.browser.manualLoading")).Attribute("data-browser-feedback", "").Attribute("aria-live", "polite"),
                new BrowserLabel(new Text("admin.browser.manualText")).Attribute("for", "remote-browser-text"),
                new BrowserTextArea().Attribute("id", "remote-browser-text").Attribute("data-browser-text", "").Attribute("maxlength", "1000").Attribute("rows", "2")
                    .Attribute("autocomplete", "off"),
                new Div(
                    new Button(new Text("admin.browser.manualSend")).Attribute("type", "button").Attribute("data-browser-send", ""),
                    new Button(new Text("admin.browser.manualTab")).Attribute("type", "button").Attribute("data-browser-key", "Tab"),
                    new Button(new Text("admin.browser.manualEnter")).Attribute("type", "button").Attribute("data-browser-key", "Enter"),
                    new Button(new Text("admin.browser.manualUp")).Attribute("type", "button").Attribute("data-browser-wheel", "-600"),
                    new Button(new Text("admin.browser.manualDown")).Attribute("type", "button").Attribute("data-browser-wheel", "600")).Class("remote-browser__tools"),
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.manualClose"), "/Admin/Browser?handler=CloseView&leaseId=" + view.LeaseId.ToString("D"), csrf, new BrowserParametersV1()),
                new BrowserNoScript(new P(new Text("admin.browser.manualJavascript"))))
                .Class("remote-browser").Attribute("data-remote-browser", address).Attribute("data-csrf", csrf ?? "")
                .Attribute("data-width", view.Width.ToString(CultureInfo.InvariantCulture)).Attribute("data-height", view.Height.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-failed", texts["admin.browser.manualFailed"]).Attribute("data-ready", texts["admin.browser.manualReady"]);
            content.WriteTo(writer, encoder, texts);
        }

        private sealed class BrowserImage() : HtmlElement("img");
        private sealed class BrowserLabel(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class BrowserTextArea() : HtmlElement("textarea");
        private sealed class BrowserNoScript(params IUiComponent[] children) : HtmlElement("noscript", children);
    }
}
