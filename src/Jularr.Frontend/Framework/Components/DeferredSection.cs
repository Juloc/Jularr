using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A part of a page that would keep the page above its 0.03 s budget loads right after it: the page answers with a skeleton of the
    // part's height, and Deferred.entry.ts swaps in the fragment from the address (a page handler that renders only this part).
    // The height is fixed, so nothing below moves when the part arrives; a failure says so and offers to try again.
    // Inline styles are not allowed, so the height is one of the classes from shell.entry.scss: a multiple of 40 px up to 800 px.
    public sealed class DeferredSection(string address, int height) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (height < 40 || height > 800 || height % 40 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "A deferred part is 40 to 800 px high, in steps of 40 px.");
            }

            new Div(new Span().Class("skeleton-block deferred-section__skeleton").Attribute("aria-hidden", "true"))
                .Class("deferred-section deferred-section--h" + height.ToString(CultureInfo.InvariantCulture))
                .Attribute("data-deferred", address)
                .Attribute("aria-busy", "true")
                .TextAttribute("data-failed", "deferred.failed")
                .TextAttribute("data-retry", "deferred.retry")
                .WriteTo(writer, encoder, texts);
        }
    }
}
