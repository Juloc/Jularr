using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The eye at the end of a password field: switches the field between hidden and readable (PasswordReveal.entry.ts).
    public sealed class PasswordReveal(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Button(new Icon("eye", assets, 18)).Class("password-reveal")
                .Attribute("aria-label", texts["account.login.showPassword"]).Attribute("aria-pressed", "false")
                .Attribute("data-password-reveal", "").Attribute("data-show-label", texts["account.login.showPassword"])
                .Attribute("data-hide-label", texts["account.login.hidePassword"])
                .WriteTo(writer, encoder, texts);
    }
}
