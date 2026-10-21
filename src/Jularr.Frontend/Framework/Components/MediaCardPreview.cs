using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A card's delayed hover preview, as the server renders it when the hover has lasted (the script makes it a popover over the card): a trailer area (the backdrop until the
    // muted trailer plays, with its own play/pause and mute controls) and the linked title with the description, or the description alone for a
    // title without a trailer. The card's own action and heart are moved into the empty slot of the foot by the script; the Details link is the foot's.
    public sealed class MediaCardPreview(FrontendAssets assets, string title, string href, string? backdropUrl, string? posterUrl, string? overview, string? trailerId) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (trailerId is not null && (trailerId.Length != 11 || trailerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
                throw new ArgumentException("Invalid YouTube video identifier.", nameof(trailerId));

            var parts = new List<IUiComponent>();
            var art = backdropUrl ?? posterUrl;
            if (trailerId is not null && art is not null)
            {
                parts.Add(new Div(new MediaImage(art, "", 640, 360), new Div().Class("media-card__trailer").Attribute("data-card-trailer", ""), Controls(texts)).Class("media-card__preview-media"));
            }

            var body = new List<IUiComponent> { new H3(new A(new ValueText(title)).Attribute("href", href)) };
            if (!string.IsNullOrWhiteSpace(overview)) body.Add(new P(new ValueText(overview)).Class("media-card__description"));
            parts.Add(new Div([.. body]).Class("media-card__preview-body"));
            parts.Add(new Div(new Div().Class("media-card__preview-extra"),
                new A(new Icon("info", assets)).Class("button button--icon media-card__preview-details").Attribute("href", href).Attribute("aria-label", texts["media.details"])).Class("media-card__preview-actions"));

            var content = new Div([.. parts]).Class("media-card__preview").Attribute("data-preview-mode", trailerId is not null && art is not null ? "trailer" : "description");
            if (trailerId is not null && art is not null) content.Attribute("data-trailer-id", trailerId).Attribute("data-trailer-label", texts["media.preview.trailer"]);
            content.WriteTo(writer, encoder, texts);
        }

        // Play or pause and sound on or off, as in the player: the icon and the label show the current state of the trailer (starting muted and playing).
        private HtmlElement Controls(UiText texts) => new Div(
            new Button(new Icon("player-pause", assets), new Icon("player-play", assets)).Class("media-card__trailer-button").Attribute("data-trailer-action", "toggle")
                .Attribute("aria-label", texts["media.preview.pauseTrailer"]).Attribute("data-label-on", texts["media.preview.pauseTrailer"]).Attribute("data-label-off", texts["media.preview.playTrailer"]),
            new Button(new Icon("player-volume", assets), new Icon("player-muted", assets)).Class("media-card__trailer-button").Attribute("data-trailer-action", "mute")
                .Attribute("aria-label", texts["media.preview.unmuteTrailer"]).Attribute("data-label-on", texts["media.preview.unmuteTrailer"]).Attribute("data-label-off", texts["media.preview.muteTrailer"]))
            .Class("media-card__trailer-controls");
    }
}
