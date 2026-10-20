using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The page's one save action for all forms declared with Form.Save; it shows only while a value differs from the loaded page.
    public sealed class SavePill : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(
                new Span().Class("save-pill__count").Attribute("aria-live", "polite")
                    .TextAttribute("data-one", "savePill.one").TextAttribute("data-other", "savePill.other")
                    .TextAttribute("data-failed", "savePill.failed").TextAttribute("data-leave", "savePill.leave"),
                new Button(new Text("savePill.discard")).Attribute("data-save-discard", ""),
                new Button(new Text("common.save")).Primary().Attribute("data-save-submit", "").TextAttribute("data-saved", "savePill.saved"))
            .Class("save-pill").Attribute("data-save-pill", "").TextAttribute("aria-label", "savePill.region").Attribute("role", "region").Attribute("hidden", "hidden")
            .WriteTo(writer, encoder, texts);
    }
}
