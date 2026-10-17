using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Shared artwork and consumption context.
    public sealed class MediaCard(string title, string href, string? posterUrl, string? meta, string statusKey, string? actionKey = null, string? actionHref = null) : IUiComponent
    {
        private string? _seriesStatusKey;
        private IUiComponent? _seriesIcon;
        private MediaAvailability? _availability;
        private IUiComponent[] _genres = [];
        private IUiComponent? _languages;
        private long? _positionMs;
        private long? _durationMs;
        private decimal? _progression;
        private string? _previewId;
        private string? _overview;
        private string? _originalTitle;
        private string? _rating;
        private string? _trailerId;
        private IUiComponent[] _actions = [];
        private IUiComponent? _bookmark;
        private bool _minimal;

        public MediaCard Minimal()
        {
            _minimal = true;
            return this;
        }

        public MediaCard Information(string? overview, string? rating = null, string? originalTitle = null)
        {
            _overview = overview;
            _rating = rating;
            _originalTitle = originalTitle;
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
                throw new ArgumentException("Invalid YouTube video identifier.", nameof(youtubeId));

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

        // Reading share, not time.
        public MediaCard ContinueReading(decimal? progression)
        {
            _positionMs = 0;
            _progression = progression;
            return this;
        }

        public MediaCard SeriesStatus(string key, IUiComponent? icon = null)
        {
            _seriesStatusKey = key;
            _seriesIcon = icon;
            return this;
        }

        public MediaCard Availability(MediaAvailability availability)
        {
            _availability = availability;
            return this;
        }

        public MediaCard Genres(params IUiComponent[] genres)
        {
            _genres = genres.Take(3).Select(genre => genre is Tag ? genre : (IUiComponent)new Tag(genre).Neutral()).ToArray();
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
            var content = new List<IUiComponent>
            {
                new A(art).Class("media-card__link media-card__art").Attribute("href", href).Attribute("aria-label", title)
            };
            if (_trailerId is not null) content.Add(new Div().Class("media-card__trailer").Attribute("data-hover-trailer", "true"));

            // Independent corner links.
            if (!_minimal && _seriesStatusKey is not null)
            {
                var tag = new Tag(_seriesIcon is null ? [new Text(_seriesStatusKey)] : [_seriesIcon, new Text(_seriesStatusKey)]).Neutral();
                IUiComponent series = _previewId is null
                    ? new Span(tag).Class("media-card__series")
                    : new A(tag).Class("media-card__series media-card__series--link").Attribute("href", href)
                        .Attribute("aria-haspopup", "dialog").Attribute("data-preview-open", _previewId);
                content.Add(series);
            }
            if (_availability is not null)
            {
                content.Add(_availability.Badge(href, _previewId));
            }
            if (_bookmark is not null) content.Add(new Div(_bookmark).Class("media-card__bookmark"));

            // Supplied actions, same services.
            var information = new List<IUiComponent>();
            var actions = new List<IUiComponent>();
            if (actionKey is not null)
            {
                IUiComponent action = actionHref is null
                    ? new Button(new Text(actionKey)).Disabled().Class("button button--primary media-card__action")
                    : new A(new Text(actionKey)).Class("button button--primary media-card__action").Attribute("href", actionHref);
                actions.Add(action);
            }
            actions.AddRange(_actions);

            // Supplied metadata only.
            var heading = new List<IUiComponent>
            {
                new H3(new A(new ValueText(title)).Attribute("href", href)).Class("media-card__title")
            };
            if (!_minimal && actions.Count > 0) heading.Add(new Div(actions[0]).Class("media-card__primary-action"));
            information.Add(new Div([.. heading]).Class("media-card__heading"));
            if (!_minimal && _originalTitle is { Length: > 0 } && !string.Equals(_originalTitle, title, StringComparison.Ordinal))
                information.Add(new Span(new ValueText(_originalTitle)).Class("media-card__original-title"));
            var metadata = new List<IUiComponent>();
            if (meta is not null) metadata.Add(new Span(new ValueText(meta)));
            if (_rating is not null) metadata.Add(new Span(new ValueText(_rating)).Attribute("aria-label", texts["media.rating"] + ": " + _rating));
            if (metadata.Count > 0) information.Add(new Div([.. metadata]).Class("media-card__meta"));
            if (!_minimal)
            {
                if (_overview is not null) information.Add(new P(new ValueText(_overview)).Class("media-card__overview"));
                if (_genres.Length > 0) information.Add(new Div(_genres).Class("media-card__genres"));
            }
            if (actions.Count > (_minimal ? 0 : 1)) information.Add(new Div(_minimal ? [.. actions] : [.. actions.Skip(1)]).Class("media-card__actions"));
            if (!_minimal && _languages is not null) information.Add(_languages);

            // Informational resume progress.
            if (_positionMs is { } position && _durationMs is > 0 and { } duration)
            {
                var boundedPosition = Math.Clamp(position, 0, duration);
                content.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", boundedPosition.ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progress"]).Class("media-card__progress"));
            }
            else if (_progression is { } share)
            {
                var percent = ReadPercent(share);
                content.Add(new Progress().Attribute("max", "100").Attribute("value", percent.ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("media-card__progress"));
            }

            // Unknown detail keeps coarse status.
            if (_availability is null && statusKey != "media.state.NotRequested") information.Add(new Span(new Text(statusKey)).Class("media-card__state"));
            content.Add(new Div([.. information]).Class("media-card__content"));

            var surface = new Div([.. content]).Class("media-card__surface").Attribute("tabindex", "0").Attribute("aria-label", title)
                .Attribute("data-media-card", "true");
            if (_trailerId is not null) surface.Attribute("data-trailer-id", _trailerId).Attribute("data-trailer-label", texts["media.preview.playTrailer"]);

            var variant = _positionMs is not null ? " media-card--continue" : _availability is not null ? " media-card--library" : "";
            if (_minimal) variant += " media-card--minimal";
            new Div(surface).Class("media-card" + variant).WriteTo(writer, encoder, texts);
        }

        public static int ReadPercent(decimal progression) => (int)Math.Round(Math.Clamp(progression, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }
}
