using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;

namespace Jularr.Frontend.Framework
{
    // One cover for every catalog: the cover with its local availability top left, the bookmark top right and one contextual
    // action on hover, then the linked title and one line of facts. Every text is one line; a clipped one shows in full in a tip.
    public sealed class MediaCard(FrontendAssets assets, string title, string href, string? posterUrl) : IUiComponent
    {
        private MediaAvailability? _availability;
        private int? _year;
        private string? _typeKey;
        private decimal? _rating;
        private string? _resume;
        private bool _isResume;
        private string? _actionKey;
        private string? _actionHref;
        private IUiComponent? _action;
        private long? _positionMs;
        private long? _durationMs;
        private decimal? _progression;
        private int? _percent;
        private string? _previewId;
        private string? _hoverPreview;
        private IUiComponent? _bookmark;
        private IUiComponent? _label;

        // A title of the profile's library, favorites or collections with everything the Backend knows of it.
        public static MediaCard For(FrontendAssets assets, LibraryItemV1 item)
        {
            var card = new MediaCard(assets, item.Title, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl)
                .Facts(item.ReleaseYear, MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId), item.Rating)
                .Availability(new MediaAvailability(assets, item.AvailabilityStateId, item.AvailableUnits, item.MonitoredUnits)).HoverPreview(MediaPreviewAddress.Work(item.Id));
            return item.ProgressStateId == ProgressStateType.InProgress ? card.Progress(item.ProgressPercent) : card;
        }

        // A title a catalog or TMDB found; one the profile already holds shows what its state says.
        public static MediaCard For(FrontendAssets assets, DiscoverItemV1 item, string href) =>
            new MediaCard(assets, item.Title, href, item.PosterUrl).Facts(item.ReleaseYear, MediaTypeLabel.Key(item.MediaTypeId, null), item.Rating).State(item.MediaStateTypeId)
                .HoverPreview(item.WorkId is { } workId ? MediaPreviewAddress.Work(workId) : item.TmdbId is { } tmdbId ? MediaPreviewAddress.Tmdb(item.MediaTypeId, tmdbId) : null);

        // The year, the type and the rating; whatever is unknown is left out.
        public MediaCard Facts(int? year, string? typeKey, decimal? rating)
        {
            _year = year;
            _typeKey = typeKey;
            _rating = rating;
            return this;
        }

        // A card to continue: the line reads "Type Year | detail" and the detail says where the profile is.
        public MediaCard Resume(string? detail)
        {
            _isResume = true;
            _resume = detail;
            return this;
        }

        // The coarse state of a title the Backend has no unit counts for; a missing title shows no tag.
        public MediaCard State(MediaStateType state)
        {
            _availability ??= state switch
            {
                MediaStateType.Available => new MediaAvailability(assets, MediaAvailabilityState.Available),
                MediaStateType.Requested => new MediaAvailability(assets, MediaAvailabilityState.Requested),
                _ => null
            };
            return this;
        }

        public MediaCard Availability(MediaAvailability availability)
        {
            _availability = availability;
            return this;
        }

        // The one contextual action: request, watch, continue or read.
        public MediaCard Action(string key, string target)
        {
            _actionKey = key;
            _actionHref = target;
            return this;
        }

        // A control of the page instead of the standard link.
        public MediaCard Action(IUiComponent action)
        {
            _action = action;
            return this;
        }

        public MediaCard Bookmark(IUiComponent action)
        {
            _bookmark = action;
            return this;
        }

        // A short label over the cover, bottom left.
        public MediaCard Label(IUiComponent label)
        {
            _label = label;
            return this;
        }

        // Where the delayed hover over the cover fetches its description or trailer; none means the card has no hover preview.
        public MediaCard HoverPreview(string? address)
        {
            _hoverPreview = address;
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

        public MediaCard Progress(int percent)
        {
            _percent = Math.Clamp(percent, 0, 100);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var art = MediaImage.Or(assets, posterUrl, title, 342, 513);
            var cover = new List<IUiComponent>
            {
                new A(art).Class("media-card__art").Attribute("href", href).Attribute("aria-label", title)
            };

            // Local availability top left, bookmark top right.
            if (_availability is not null) cover.Add(_availability.Badge(href, _previewId));
            if (_bookmark is not null) cover.Add(new Div(_bookmark).Class("media-card__bookmark"));
            if (_label is not null) cover.Add(new Div(_label).Class("media-card__label"));

            // Only a supplied action, never an invented one.
            var action = _action;
            if (action is null && _actionKey is not null && _actionHref is not null)
            {
                var icon = _actionKey is "request.create" or "media.request" or "discover.action.request" ? "requests"
                    : _actionHref.StartsWith("/Read/", StringComparison.Ordinal) ? "reading" : "player-play";
                action = new A(new Icon(icon, assets, 16), new Span(new Text(_actionKey)).Class("media-card__primary-label"))
                    .Class("button button--primary button--compact media-card__primary").Attribute("href", _actionHref);
            }

            if (action is not null) cover.Add(new Div(action).Class("media-card__actions"));

            // Progress is a thin capsule and never moves other content.
            var progress = ProgressBar(texts);
            if (progress is not null) cover.Add(new Div(progress).Class("media-card__progress-shell"));

            var surface = new Div([.. cover]).Class("media-card__surface").Attribute("tabindex", "0")
                .Attribute("aria-label", title).Attribute("data-media-card", "true");

            // The title links to the details and the facts follow in one line.
            var details = new List<IUiComponent>
            {
                new H3(new A(new ValueText(title)).Class("media-card__link").Attribute("href", href)).Class("media-card__title").Attribute("data-full", title)
            };
            var info = Info(texts);
            if (info is not null) details.Add(info);
            details.Add(new Span().Class("media-card__tip").Attribute("role", "presentation").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"));

            var card = new Div(surface, new Div([.. details]).Class("media-card__details")).Class(_isResume ? "media-card media-card--resume" : "media-card");
            if (_hoverPreview is not null) card.Attribute("data-preview-src", _hoverPreview);
            card.WriteTo(writer, encoder, texts);
        }

        private HtmlElement? ProgressBar(UiText texts)
        {
            if (_positionMs is { } position && _durationMs is > 0 and { } duration)
            {
                return new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", Math.Clamp(position, 0, duration).ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progress"]).Class("media-card__progress");
            }

            if (_progression is { } share)
            {
                return new Progress().Attribute("max", "100").Attribute("value", ReadPercent(share).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("media-card__progress");
            }

            return _percent is { } percent
                ? new Progress().Attribute("max", "100").Attribute("value", percent.ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progressShare"]).Class("media-card__progress")
                : null;
        }

        // Year · type · rating, or for a card to continue "Type Year | detail".
        private HtmlElement? Info(UiText texts)
        {
            var type = _typeKey is null ? null : texts[_typeKey];
            var year = _year?.ToString(CultureInfo.InvariantCulture);
            if (_isResume)
            {
                var lead = string.Join(' ', new[] { type, year }.Where(part => part is not null));
                if (lead.Length == 0 && _resume is null) return null;

                var parts = new List<IUiComponent>();
                if (lead.Length > 0) parts.Add(new Span(new ValueText(lead)).Class("media-card__lead"));
                if (_resume is not null) parts.Add(new Span(new ValueText(_resume)).Class("media-card__detail"));
                return new P([.. parts]).Class("media-card__info media-card__info--resume").Attribute("data-full", string.Join(" | ", new[] { lead.Length > 0 ? lead : null, _resume }.Where(part => part is not null)));
            }

            var rating = _rating?.ToString("0.0", CultureInfo.CurrentCulture);
            if (year is null && type is null && rating is null) return null;

            var facts = new List<IUiComponent>();
            if (year is not null) facts.Add(new Span(new ValueText(year)).Class("media-card__year"));
            if (type is not null) facts.Add(new Span(new ValueText(type)).Class("media-card__type"));
            if (rating is not null) facts.Add(new Span(new Icon("star", assets, 11), new ValueText(rating)).Class("media-card__rating"));
            return new P([.. facts]).Class("media-card__info").Attribute("data-full", string.Join(" · ", new[] { year, type, rating }.Where(part => part is not null)));
        }

        public static int ReadPercent(decimal progression) => (int)Math.Round(Math.Clamp(progression, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }
}
