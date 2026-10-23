using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The page's one save action for all forms declared with Form.Save: the action dock in its form-page mode, shown only while a value differs from the loaded page.
    public sealed class SavePill : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) => ActionDock.ForForm().WriteTo(writer, encoder, texts);
    }
}
