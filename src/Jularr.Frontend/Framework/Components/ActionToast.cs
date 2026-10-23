using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The quiet hint that confirms an action for about two seconds (saved, started, nothing found): a pill at the bottom centre in the look of the
    // save pill. The server renders the text itself when a page without script answers an action; with script the hint arrives with the
    // response (HX-Trigger jularr:hint, see JularrPage.Hint) and ActionToast.ts shows it.
    public sealed class ActionToast(string? text = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Div(new Span(text is null ? new Span() : new ValueText(text)).Class("action-toast__text"))
                .Class(text is null ? "action-toast" : "action-toast action-toast--show")
                .Attribute("data-action-toast", "").Attribute("role", "status").Attribute("aria-live", "polite")
                .TextAttribute("data-failed", "actionToast.failed")
                .WriteTo(writer, encoder, texts);
    }
}
