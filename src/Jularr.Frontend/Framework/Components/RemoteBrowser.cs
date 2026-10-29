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
            var image = new BrowserImage().Class("remote-browser__image").Attribute("alt", texts["admin.browser.manualImage"])
                .Attribute("tabindex", "-1").Attribute("draggable", "false").Attribute("data-browser-image", "")
                .Attribute("width", view.Width.ToString(CultureInfo.InvariantCulture)).Attribute("height", view.Height.ToString(CultureInfo.InvariantCulture));
            if (view.IsActive) image.Attribute("src", address + "/frame");
            var reopen = view.OperationId is { } operationId
                ? "/Admin/Browser?handler=OpenDownloadView&operationId=" + operationId.ToString(CultureInfo.InvariantCulture)
                : "/Admin/Browser?handler=OpenView&acquisitionConnectionId=" + view.AcquisitionConnectionId.ToString(CultureInfo.InvariantCulture);
            var reconnect = new Div(new Form<BrowserParametersV1>(form => form.Submit("admin.browser.manualReconnect"),
                reopen, csrf, new BrowserParametersV1()))
                .Attribute("data-browser-reconnect", "");
            if (view.IsActive) reconnect.Attribute("hidden", "");
            var content = new Div(
                new P(new Text("admin.browser.manualHint")),
                new P(new Text(view.IsActive ? "admin.browser.manualLoading" : "admin.browser.manualFailed")).Attribute("data-browser-feedback", "").Attribute("aria-live", "polite"),
                reconnect,
                image,
                new BrowserLabel(new Text("admin.browser.manualText")).Attribute("for", "remote-browser-text"),
                new BrowserTextArea().Attribute("id", "remote-browser-text").Attribute("data-browser-text", "").Attribute("maxlength", "1000").Attribute("rows", "2")
                    .Attribute("autocomplete", "off").Attribute("data-browser-input", ""),
                new Div(
                    new Button(new Text("admin.browser.manualSend")).Attribute("type", "button").Attribute("data-browser-send", "").Attribute("data-browser-input", ""),
                    new Button(new Text("admin.browser.manualTab")).Attribute("type", "button").Attribute("data-browser-key", "Tab").Attribute("data-browser-input", ""),
                    new Button(new Text("admin.browser.manualEnter")).Attribute("type", "button").Attribute("data-browser-key", "Enter").Attribute("data-browser-input", ""),
                    new Button(new Text("admin.browser.manualUp")).Attribute("type", "button").Attribute("data-browser-wheel", "-600").Attribute("data-browser-input", ""),
                    new Button(new Text("admin.browser.manualDown")).Attribute("type", "button").Attribute("data-browser-wheel", "600").Attribute("data-browser-input", "")).Class("remote-browser__tools"),
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.manualClose"), "/Admin/Browser?handler=CloseView&leaseId=" + view.LeaseId.ToString("D"), csrf, new BrowserParametersV1()),
                new BrowserNoScript(new P(new Text("admin.browser.manualJavascript"))))
                .Class("remote-browser").Attribute("data-remote-browser", address).Attribute("data-csrf", csrf ?? "")
                .Attribute("data-width", view.Width.ToString(CultureInfo.InvariantCulture)).Attribute("data-height", view.Height.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-active", view.IsActive ? "true" : "false").Attribute("data-retrying", texts["admin.browser.manualRetrying"])
                .Attribute("data-denied", texts["admin.browser.manualDenied"])
                .Attribute("data-failed", texts["admin.browser.manualFailed"]).Attribute("data-ready", texts["admin.browser.manualReady"]);
            content.WriteTo(writer, encoder, texts);
        }

        private sealed class BrowserImage() : HtmlElement("img");
        private sealed class BrowserLabel(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class BrowserTextArea() : HtmlElement("textarea");
        private sealed class BrowserNoScript(params IUiComponent[] children) : HtmlElement("noscript", children);
    }
}
