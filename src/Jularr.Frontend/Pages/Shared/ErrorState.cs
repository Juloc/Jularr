using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // The one error page of both skins: Clean shows the Jularr mark large above a bold "404 Not Found"; Original shows its own artwork
    // for the state instead of the mark (ErrorState.scss, switched by html[data-skin]). The texts, actions and routes are the same.
    public sealed class ErrorState(FrontendAssets assets, int code, string? retryHref, string? signInHref, string? reference, bool standalone, string? reloadHref = null) : IUiComponent
    {
        // The statuses with a page of their own; any other client error reads as 400, any other server error as 500.
        public static int Normalize(int code) => code switch
        {
            400 or 401 or 403 or 404 or 408 or 410 or 429 or 500 or 502 or 503 => code,
            >= 500 => 500,
            _ => code is 405 or 406 or 409 or 411 or 412 or 413 or 414 or 415 or 416 or 417 or 421 or 422 ? 400 : 404
        };

        // A failure of the service itself or a visitor who is not signed in never needs the application shell around it.
        public static bool NeedsStandaloneLayout(int code) => code is 401 or >= 500;

        // The artwork of Original per status: 404 the sign, 410 and 400 the empty box (closest to "gone" and "not understood"),
        // 401 and 403 the locked door, 408, 429 and 503 the switched-off panel, 500 and 502 the broken machine.
        private static string Art(int code) => code switch
        {
            404 => "404",
            400 or 410 => "not-found",
            401 or 403 => "forbidden",
            408 or 429 or 503 => "unavailable",
            _ => "500"
        };

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var normalized = Normalize(code);
            var prefix = "error." + normalized.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var children = new List<IUiComponent>
            {
                new Div().Class("error-state__art").Attribute("aria-hidden", "true"),
                new Div(Brand.Mark(assets, "brand-clean", "brand-symbol__clean")).Class("error-state__mark").Attribute("aria-hidden", "true"),
                new H1(new Text(prefix + ".title")).Class("error-state__title"),
                new P(new Text(prefix + ".text")).Class("error-state__text").Attribute("role", "alert")
            };

            if (reference is not null) children.Add(new P(new ValueText(string.Format(System.Globalization.CultureInfo.CurrentCulture, texts["error.reference"], reference))).Class("error-state__reference"));

            var home = new A(new Text("nav.home")).Attribute("href", "/");
            var back = new Button(new Text("nav.back")).Attribute("data-history-back", "").Attribute("hidden", "hidden");
            var actions = new List<IUiComponent>();
            if (normalized == 401)
            {
                actions.Add(new A(new Text("error.signIn")).Class("button button--primary").Attribute("href", signInHref ?? "/Account/Login"));
                actions.Add(back);
            }
            else if (retryHref is not null && normalized is 408 or 429 or 500 or 502 or 503)
            {
                actions.Add(new A(new Text("error.retry")).Class("button button--primary").Attribute("href", retryHref));
                actions.Add(home.Class("button"));
            }
            else
            {
                actions.Add(home.Class("button button--primary"));
                actions.Add(back);
            }

            children.Add(new Div([.. actions]).Class("error-state__actions error-state__actions--clean"));

            // Original shows its artwork over the whole page with exactly these two buttons and no other text (the title and line stay for assistive technology).
            // Retry reads the address that failed again, whatever the method was; it never repeats a write.
            children.Add(new Div(
                new A(new Text("error.retry")).Class("button button--primary").Attribute("href", reloadHref ?? "/"),
                new A(new Text("error.homePage")).Class("button").Attribute("href", "/")).Class("error-state__actions error-state__actions--original"));
            new Section([.. children]).Class("error-state error-state--" + Art(normalized) + (standalone ? " error-state--standalone" : ""))
                .Attribute("data-error-code", normalized.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WriteTo(writer, encoder, texts);
        }
    }
}
