using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The device's choice between covers in a grid and the same titles as rows (Library and Favorites). The icon shows the layout a click leads to;
    // without script the button stays hidden and the grid remains.
    public sealed class LayoutToggle(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Button(new Icon("grid", assets), new Icon("list", assets)).IconOnly(texts["list.layout.toggle"]).Class("button button--icon layout-toggle").Attribute("data-layout-toggle", "")
                .WriteTo(writer, encoder, texts);
    }
}
