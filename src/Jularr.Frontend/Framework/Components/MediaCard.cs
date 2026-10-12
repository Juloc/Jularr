using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One card grammar for every media surface: artwork, title, a little context and a fixed status area.
    public sealed class MediaCard(string title, string href, string? posterUrl, string? meta, string statusKey, string? actionKey = null, string? actionHref = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            IUiComponent art = posterUrl is null
                ? new Div(new Span(new ValueText(title))).Class("media-card__placeholder")
                : new MediaImage(posterUrl, title, 342, 513);
            var link = new A(
                new Div(art).Class("media-card__art"),
                new Span(new ValueText(title)).Class("media-card__title"),
                meta is null ? new Span() : new Span(new ValueText(meta)).Class("media-card__meta")).Class("media-card__link").Attribute("href", href);

            // The status area becomes the action on hover or focus; a card click never acquires.
            var status = new Span(new Text(statusKey)).Class("media-card__state");
            IUiComponent area = actionKey is null || actionHref is null
                ? new Div(status).Class("media-card__status")
                : new Div(status, new A(new Text(actionKey)).Class("media-card__action").Attribute("href", actionHref)).Class("media-card__status media-card__status--action");
            new Div(link, area).Class("media-card").WriteTo(writer, encoder, texts);
        }
    }
}
