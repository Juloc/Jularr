using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // A card's delayed hover preview, as the server renders it when the hover has lasted (the script makes it a popover over the card). Trailer mode: the trailer area
    // (the backdrop until the muted trailer plays, with its own pause and mute buttons and a progress line), then the linked title, the facts and two lines of description.
    // Description mode, for a title without a trailer: the larger title, the facts and the longer description. The card's own action and heart are moved into the empty
    // slot of the foot by the script; the Details button is the foot's.
    public sealed class MediaCardPreview(FrontendAssets assets, string title, string href, string? backdropUrl, string? posterUrl, string? overview, string? trailerId,
        IReadOnlyList<string>? facts = null) : IUiComponent
    {
        // Year, type and, where it is known, the runtime; whatever is unknown is left out.
        public static IReadOnlyList<string> Facts(UiText texts, int? year, MediaType type, MediaSubtype? subtype, int? runtimeMinutes)
        {
            var list = new List<string>();
            if (year is { } value) list.Add(value.ToString(CultureInfo.InvariantCulture));
            list.Add(texts[MediaTypeLabel.Key(type, subtype)]);
            if (runtimeMinutes is > 0)
            {
                list.Add(runtimeMinutes < 60
                    ? string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], runtimeMinutes)
                    : string.Format(CultureInfo.CurrentCulture, texts["media.preview.runtime"], runtimeMinutes / 60, runtimeMinutes % 60));
            }

            return list;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (trailerId is not null && (trailerId.Length != 11 || trailerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
                throw new ArgumentException("Invalid YouTube video identifier.", nameof(trailerId));

            var art = backdropUrl ?? posterUrl;
            var hasTrailer = trailerId is not null && art is not null;
            var parts = new List<IUiComponent>();
            if (hasTrailer)
            {
                parts.Add(new Div(new Div(new MediaImage(art!, "", 640, 360)).Class("media-card__preview-poster"), new Div().Class("media-card__trailer").Attribute("data-card-trailer", ""),
                    Controls(texts), Foot()).Class("media-card__preview-media"));
            }

            var body = new List<IUiComponent> { new H3(new A(new ValueText(title)).Attribute("href", href)).Class("media-card__preview-title") };
            if (facts is { Count: > 0 }) body.Add(new P([.. facts.Select(fact => (IUiComponent)new Span(new ValueText(fact)))]).Class("media-card__preview-meta"));
            if (!string.IsNullOrWhiteSpace(overview)) body.Add(new P(new ValueText(overview)).Class("media-card__description"));
            parts.Add(new Div([.. body]).Class("media-card__preview-body"));
            parts.Add(new Div(new Div().Class("media-card__preview-extra"),
                new A(new Icon("info", assets)).Class("button button--icon media-card__preview-details").Attribute("href", href).Attribute("aria-label", texts["media.details"])
                    .Attribute("title", texts["media.details"])).Class("media-card__preview-actions"));

            var content = new Div([.. parts]).Class("media-card__preview").Attribute("data-preview-mode", hasTrailer ? "trailer" : "description");
            if (hasTrailer) content.Attribute("data-trailer-id", trailerId!).Attribute("data-trailer-label", texts["media.preview.trailer"]);
            content.WriteTo(writer, encoder, texts);
        }

        // Pause top left and sound top right, as in the player: the icon and the label show the current state of the trailer (starting muted and playing).
        private HtmlElement Controls(UiText texts) => new Div(
            new Button(new Icon("player-pause", assets), new Icon("player-play", assets)).Class("media-card__trailer-button").Attribute("data-trailer-action", "toggle")
                .Attribute("aria-label", texts["media.preview.pauseTrailer"]).Attribute("title", texts["media.preview.pauseTrailer"])
                .Attribute("data-label-on", texts["media.preview.pauseTrailer"]).Attribute("data-label-off", texts["media.preview.playTrailer"]),
            new Button(new Icon("player-volume", assets), new Icon("player-muted", assets)).Class("media-card__trailer-button").Attribute("data-trailer-action", "mute")
                .Attribute("aria-label", texts["media.preview.unmuteTrailer"]).Attribute("title", texts["media.preview.unmuteTrailer"])
                .Attribute("data-label-on", texts["media.preview.unmuteTrailer"]).Attribute("data-label-off", texts["media.preview.muteTrailer"]))
            .Class("media-card__trailer-controls");

        // The thin progress line with the time at the foot of the frame; the script fills both from the provider's player.
        private static HtmlElement Foot() => new Div(new Div(new Span()).Class("media-card__trailer-line"), new Span().Class("media-card__trailer-time").Attribute("data-trailer-time", ""))
            .Class("media-card__trailer-foot");
    }
}
