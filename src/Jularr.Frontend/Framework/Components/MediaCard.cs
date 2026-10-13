using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Shared artwork and consumption context.
    public sealed class MediaCard(string title, string href, string? posterUrl, string? meta, string statusKey, string? actionKey = null, string? actionHref = null) : IUiComponent
    {
        private IUiComponent[] _libraryContext = [];
        private IUiComponent? _languages;
        private long? _positionMs;
        private long? _durationMs;

        public MediaCard Continue(long positionMs, long? durationMs)
        {
            _positionMs = positionMs;
            _durationMs = durationMs;
            return this;
        }

        public MediaCard Library(IUiComponent seriesStatus, IUiComponent availability)
        {
            _libraryContext = [new Span(seriesStatus), new Span(availability)];
            return this;
        }

        // Available preferences or language counts.
        public MediaCard Languages(string? preferredDub, int dubCount, string? preferredSub, int subCount)
        {
            _languages = new Div(
                new Span(new Text("media.dubs"), new ValueText(" " + (preferredDub ?? dubCount.ToString(CultureInfo.CurrentCulture)))),
                new Span(new Text("media.subs"), new ValueText(" " + (preferredSub ?? subCount.ToString(CultureInfo.CurrentCulture)))))
                .Class("media-card__languages");
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

            // Status remains separate from actions.
            var status = new Span(new Text(statusKey)).Class("media-card__state");
            var content = new List<IUiComponent> { link };
            if (_libraryContext.Length > 0) content.Add(new Div(_libraryContext).Class("media-card__context"));
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

            content.Add(new Div(status).Class("media-card__status"));
            if (actionKey is not null)
            {
                content.Add(actionHref is null
                    ? new Button(new Text(actionKey)).Disabled().Class("button media-card__action")
                    : new A(new Text(actionKey)).Class("button media-card__action").Attribute("href", actionHref));
            }

            var variant = _positionMs is not null ? " media-card--continue" : _libraryContext.Length > 0 ? " media-card--library" : "";
            new Div([.. content]).Class("media-card" + variant).WriteTo(writer, encoder, texts);
        }

        private sealed class Progress() : HtmlElement("progress");
    }
}
