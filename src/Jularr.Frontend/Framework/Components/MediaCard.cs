using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One cover for every catalog.
    public sealed class MediaCard(FrontendAssets assets, string title, string href, string? posterUrl, string? meta, string statusKey, string? actionKey = null, string? actionHref = null) : IUiComponent
    {
        private MediaAvailability? _availability;
        private long? _positionMs;
        private long? _durationMs;
        private decimal? _progression;
        private string? _previewId;
        private string? _rating;
        private string? _trailerId;
        private IUiComponent[] _actions = [];
        private IUiComponent? _bookmark;

        public MediaCard Rating(string? rating)
        {
            _rating = rating;
            return this;
        }

        public MediaCard Bookmark(IUiComponent action)
        {
            _bookmark = action;
            return this;
        }

        public MediaCard Trailer(string youtubeId)
        {
            if (youtubeId.Length != 11 || youtubeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new ArgumentException("Invalid YouTube video identifier.", nameof(youtubeId));
            }

            _trailerId = youtubeId;
            return this;
        }

        public MediaCard Actions(params IUiComponent[] actions)
        {
            _actions = actions;
            return this;
        }

        public MediaCard Preview(string id)
        {
            _previewId = id;
            return this;
        }

        public MediaCard Continue(long positionMs, long? durationMs)
        {
            _positionMs = positionMs;
            _durationMs = durationMs;
            return this;
        }

        public MediaCard ContinueReading(decimal? progression)
        {
            _progression = progression;
            return this;
        }

        public MediaCard Availability(MediaAvailability availability)
        {
            _availability = availability;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            IUiComponent art = posterUrl is null
                ? new Div(new Span(new ValueText(title))).Class("media-card__placeholder")
                : new MediaImage(posterUrl, title, 342, 513);
            var content = new List<IUiComponent>
            {
                new A(art).Class("media-card__art").Attribute("href", href).Attribute("aria-label", title)
            };
            if (_trailerId is not null) content.Add(new Div().Class("media-card__trailer").Attribute("data-hover-trailer", "true"));

            // Independent corner controls.
            if (_availability is not null) content.Add(_availability.Badge(href, _previewId));
            else if (statusKey == "media.state.Requested") content.Add(new MediaAvailability(assets, MediaAvailabilityState.Requested).Badge(href, _previewId));
            if (_bookmark is not null) content.Add(new Div(_bookmark).Class("media-card__bookmark"));

            // Only supplied playable/request actions.
            var actions = new List<IUiComponent>();
            if (actionKey is not null && actionHref is not null)
            {
                var icon = actionKey is "request.create" or "media.request" ? "requests" : actionHref.StartsWith("/Read/", StringComparison.Ordinal) ? "reading" : "player-play";
                actions.Add(new A(new Icon(icon, assets), new Text(actionKey)).Class("button button--primary button--compact").Attribute("href", actionHref));
            }
            actions.AddRange(_actions);
            if (_actions.Length == 0)
            {
                var details = new A(new Icon("library", assets), new Text("media.details")).Class("button button--compact").Attribute("href", href);
                if (_previewId is not null) details.Attribute("data-preview-open", _previewId).Attribute("aria-haspopup", "dialog");
                actions.Add(details);
            }

            // Compact hover information.
            var information = new List<IUiComponent>
            {
                new H3(new A(new ValueText(title)).Attribute("href", href)).Class("media-card__title")
            };
            var metadata = new List<IUiComponent>();
            if (meta is not null) metadata.Add(new Span(new ValueText(meta)));
            if (_rating is not null) metadata.Add(new Span(new ValueText(_rating)).Attribute("aria-label", texts["media.rating"] + ": " + _rating));
            if (metadata.Count > 0) information.Add(new Div([.. metadata]).Class("media-card__meta"));
            information.Add(new Div([.. actions]).Class("media-card__actions"));

            // Do not invent local completeness.
            if (_availability is null && statusKey != "media.state.NotRequested" && statusKey != "media.state.Requested")
            {
                information.Add(new Span(new Text(statusKey)).Class("media-card__state"));
            }
            content.Add(new Div([.. information]).Class("media-card__content"));

            // Progress never changes layout.
            if (_positionMs is { } position && _durationMs is > 0 and { } duration)
            {
                content.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", Math.Clamp(position, 0, duration).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.progress"]).Class("media-card__progress"));
            }
            else if (_progression is { } share)
            {
                content.Add(new Progress().Attribute("max", "100").Attribute("value", ReadPercent(share).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("media-card__progress"));
            }

            var surface = new Div([.. content]).Class("media-card__surface").Attribute("tabindex", "0").Attribute("aria-label", title)
                .Attribute("data-media-card", "true");
            if (_trailerId is not null)
            {
                surface.Attribute("data-trailer-id", _trailerId).Attribute("data-trailer-label", texts["media.preview.playTrailer"]);
            }
            new Div(surface).Class("media-card").WriteTo(writer, encoder, texts);
        }

        public static int ReadPercent(decimal progression) => (int)Math.Round(Math.Clamp(progression, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }
}
