using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Frontend.Framework
{
    // The request of a card as one round icon: a click requests the title at once with the profile's defaults, and the icon then follows the live
    // phase of the profile's request (plus, a clock while it waits for approval, a pulsing search, the download's percent in a ring, play).
    // A title already requested leads to its page instead, and so does the button once its request runs: the title's page shows the progress.
    // The phase as text shows only in the hover preview's pill.
    public sealed class CardRequest(FrontendAssets assets, long workId, string? antiforgeryToken, AcquisitionRequestStatusType? phase) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var id = workId.ToString(CultureInfo.InvariantCulture);
            var label = texts[phase is null ? "discover.action.request" : "request.state." + phase];
            var icons = new IUiComponent[]
            {
                new Span(new Span(new Icon("plus", assets, 18)).Class("card-request__plus"), new Span(new Icon("clock", assets, 18)).Class("card-request__wait"),
                    new Span(new Icon("search", assets, 18)).Class("card-request__search"),
                    new Span(new Icon("download", assets, 18)).Class("card-request__download"),
                    new Span(new Icon("player-play", assets, 18)).Class("card-request__done")).Class("card-request__icons"),
                new Span().Class("card-request__ring").Attribute("data-live-value", "percent").Attribute("hidden", "hidden").Attribute("aria-hidden", "true"),
                new Span(new ValueText(label)).Class("media-card__primary-label card-request__label").Attribute("data-live-text", "phase")
            };

            // Requested: a link to the title's page, where the request shows its progress, with the live phase.
            if (phase is { } current)
            {
                new A(icons).Class("button button--primary button--icon media-card__primary card-request").Attribute("href", "/Details/" + id)
                    .Attribute("aria-label", label).Attribute("title", label).Attribute("data-live", LiveKeys.RequestOfWork(workId))
                    .Attribute("data-phase", LiveFacts.Marker(current)).WriteTo(writer, encoder, texts);
                return;
            }

            // Not yet: the button posts the quick request and the live phase takes over.
            var button = new Button(icons).Submit();
            button.Class("button button--primary button--icon media-card__primary card-request").Attribute("aria-label", label).Attribute("title", label)
                .Attribute("data-live", LiveKeys.RequestOfWork(workId));
            new FormElement(new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", antiforgeryToken ?? ""), button)
                .Attribute("method", "post").Attribute("action", "/Details/" + id + "?handler=QuickRequest").Attribute("hx-boost", "false")
                .Attribute("data-quick-request", "true").Attribute("data-details", "/Details/" + id).Attribute("data-failed", texts["request.quick.failed"])
                .Class("card-request-form").WriteTo(writer, encoder, texts);
        }
    }
}
