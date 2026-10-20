using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Media;

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
        private IUiComponent[] _actions = [];
        private IUiComponent? _bookmark;

        public MediaCard Bookmark(IUiComponent action)
        {
            _bookmark = action;
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
            var poster = new List<IUiComponent>
            {
                new A(art).Class("media-card__art").Attribute("href", href).Attribute("aria-label", title)
            };

            // Keep the existing local status and bookmark controls.
            if (_availability is not null) poster.Add(_availability.Badge(href, _previewId));
            else if (statusKey == "media.state.Requested") poster.Add(new MediaAvailability(assets, MediaAvailabilityState.Requested).Badge(href, _previewId));
            if (_bookmark is not null) poster.Add(new Div(_bookmark).Class("media-card__bookmark"));

            // Only one contextual action is shown in the center of the poster.
            IUiComponent? action = _actions.FirstOrDefault();
            if (action is null && actionKey is not null && actionHref is not null)
            {
                var icon = actionKey is "request.create" or "media.request" ? "requests"
                    : actionHref.StartsWith("/Read/", StringComparison.Ordinal) ? "reading" : "player-play";
                action = new A(new Icon(icon, assets), new Text(actionKey)).Class("button button--primary button--compact").Attribute("href", actionHref);
            }
            if (action is not null) poster.Add(new Div(action).Class("media-card__actions"));

            // Progress stays at the bottom of the fixed-ratio poster.
            if (_positionMs is { } position && _durationMs is > 0 and { } duration)
            {
                poster.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", Math.Clamp(position, 0, duration).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.progress"]).Class("media-card__progress"));
            }
            else if (_progression is { } share)
            {
                poster.Add(new Progress().Attribute("max", "100").Attribute("value", ReadPercent(share).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("media-card__progress"));
            }

            // The title is always visible outside the poster and links to Details.
            var details = new List<IUiComponent>
            {
                new H3(new A(new ValueText(title)).Attribute("href", href)).Class("media-card__title")
            };
            if (meta is not null) details.Add(new Div(new Span(new ValueText(meta))).Class("media-card__meta"));
            if (_availability is null && statusKey != "media.state.NotRequested" && statusKey != "media.state.Requested")
                details.Add(new Span(new Text(statusKey)).Class("media-card__state"));

            var surface = new Div([.. poster]).Class("media-card__surface").Attribute("tabindex", "0")
                .Attribute("aria-label", title).Attribute("data-media-card", "true");
            new Div(surface, new Div([.. details]).Class("media-card__details")).Class("media-card").WriteTo(writer, encoder, texts);
        }

        public static int ReadPercent(decimal progression) => (int)Math.Round(Math.Clamp(progression, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }
}
