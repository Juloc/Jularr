using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Shared artwork and consumption context.
    public sealed class MediaCard(string title, string href, string? posterUrl, string? meta, string statusKey, string? actionKey = null, string? actionHref = null) : IUiComponent
    {
        private string? _seriesStatusKey;
        private MediaAvailability? _availability;
        private IUiComponent[] _genres = [];
        private IUiComponent? _languages;
        private long? _positionMs;
        private long? _durationMs;
        private decimal? _progression;
        private string? _previewId;

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

        // Books and comics: the read share instead of time.
        public MediaCard ContinueReading(decimal? progression)
        {
            _positionMs = 0;
            _progression = progression;
            return this;
        }

        public MediaCard SeriesStatus(string key)
        {
            _seriesStatusKey = key;
            return this;
        }

        public MediaCard Availability(MediaAvailability availability)
        {
            _availability = availability;
            return this;
        }

        public MediaCard Genres(params IUiComponent[] genres)
        {
            _genres = genres.Take(3).Select(genre => (IUiComponent)new Tag(genre)).ToArray();
            return this;
        }

        public MediaCard Languages(FrontendAssets assets, MediaLanguage? preferredDub, int dubCount, MediaLanguage? preferredSub, int subCount)
        {
            _languages = new MediaLanguages(assets, preferredDub, dubCount, preferredSub, subCount);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            IUiComponent art = posterUrl is null
                ? new Div(new Span(new ValueText(title))).Class("media-card__placeholder")
                : new MediaImage(posterUrl, title, 342, 513);
            var link = new A(
                new Div(art).Class("media-card__art"),
                new Span(new ValueText(title)).Class("media-card__title"),
                meta is null ? new Span() : new Span(new ValueText(meta)).Class("media-card__meta")).Class("media-card__link").Attribute("href", href);

            // Artwork overlays, separate links.
            var content = new List<IUiComponent> { link };
            var overlays = new List<IUiComponent>();
            if (_seriesStatusKey is not null)
            {
                IUiComponent series = _previewId is null
                    ? new Span(new Tag(new Text(_seriesStatusKey))).Class("media-card__series")
                    : new A(new Tag(new Text(_seriesStatusKey))).Class("media-card__series media-card__series--link").Attribute("href", href)
                        .Attribute("aria-haspopup", "dialog").Attribute("data-preview-open", _previewId);
                overlays.Add(series);
            }
            if (_availability is not null)
            {
                var info = new IconInfo(_availability, _availability.GetLabel(texts));
                if (_previewId is not null) info.Preview(_previewId, href);
                overlays.Add(new Div(info).Class("media-card__availability"));
            }
            if (actionKey is not null)
            {
                IUiComponent action = actionHref is null
                    ? new Button(new Text(actionKey)).Disabled().Class("button button--primary media-card__action")
                    : new A(new Text(actionKey)).Class("button button--primary media-card__action").Attribute("href", actionHref);
                overlays.Add(new Div(action).Class("media-card__action-area"));
            }
            if (overlays.Count > 0) content.Add(new Div([.. overlays]).Class("media-card__overlay"));
            if (_genres.Length > 0) content.Add(new Div(_genres).Class("media-card__genres"));
            if (_languages is not null) content.Add(_languages);

            // Informational resume progress.
            if (_positionMs is { } position && _durationMs is > 0 and { } duration)
            {
                var boundedPosition = Math.Clamp(position, 0, duration);
                content.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", boundedPosition.ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progress"]).Class("media-card__progress"));
                content.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["media.minutesLeft"],
                    Math.Ceiling((duration - boundedPosition) / 60000m)))).Class("media-card__meta"));
            }
            else if (_progression is { } share)
            {
                var percent = ReadPercent(share);
                content.Add(new Progress().Attribute("max", "100").Attribute("value", percent.ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("media-card__progress"));
                content.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["media.percentRead"], percent))).Class("media-card__meta"));
            }

            // Unknown detail keeps coarse status.
            if (_availability is null) content.Add(new Div(new Span(new Text(statusKey)).Class("media-card__state")).Class("media-card__status"));

            var variant = _positionMs is not null ? " media-card--continue" : _availability is not null ? " media-card--library" : "";
            new Div([.. content]).Class("media-card" + variant).WriteTo(writer, encoder, texts);
        }

        public static int ReadPercent(decimal progression) => (int)Math.Round(Math.Clamp(progression, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }
}
