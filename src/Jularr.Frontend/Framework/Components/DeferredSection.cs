using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A part of a page that would keep the page above its 0.03 s budget loads right after it: the page answers with a skeleton of the
    // part's height, and Deferred.entry.ts swaps in the fragment from the address (a page handler that renders only this part).
    // The height is fixed, so nothing below moves when the part arrives; a failure says so and offers to try again.
    public sealed class DeferredSection(string address, int height) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Div(new Span().Class("skeleton-block deferred-section__skeleton").Attribute("aria-hidden", "true"))
                .Class("deferred-section")
                .Attribute("data-deferred", address)
                .Attribute("aria-busy", "true")
                .Attribute("style", "min-height: " + height.ToString(CultureInfo.InvariantCulture) + "px")
                .TextAttribute("data-failed", "deferred.failed")
                .TextAttribute("data-retry", "deferred.retry")
                .WriteTo(writer, encoder, texts);
        }
    }
}
