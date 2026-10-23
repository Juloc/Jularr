using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Appearance
{
    // The two round buttons of the sign-in pages: the design (Clean, Original) and the brightness (system, light, dark) each go to the next choice
    // (theme.entry.ts) and are remembered on the device.
    public sealed class AppearanceDock(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Div(
                new Button(new Icon("palette", assets, 21)).Class("appearance-dock__button")
                    .Attribute("data-cycle-skin", "").Attribute("aria-label", texts["appearance.design"]).Attribute("title", texts["appearance.design"]),
                new Button(
                    new Span(new Icon("theme-system", assets, 21)).Class("appearance-dock__system"),
                    new Span(new Icon("theme-light", assets, 21)).Class("appearance-dock__light"),
                    new Span(new Icon("theme-dark", assets, 21)).Class("appearance-dock__dark")).Class("appearance-dock__button")
                    .Attribute("data-cycle-mode", "").Attribute("aria-label", texts["appearance.theme"]).Attribute("title", texts["appearance.theme"]))
                .Class("appearance-dock").WriteTo(writer, encoder, texts);
        }
    }
}
