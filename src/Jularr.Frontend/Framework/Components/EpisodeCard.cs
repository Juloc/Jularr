using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One episode: its still with length and progress, number, title and summary, and its state; a playable one opens the player.
    public sealed class EpisodeCard(string label, string? title, string? overview, string? imageUrl, int? runtimeMinutes, string stateKey, string? playHref, string? resumeAt = null) : IUiComponent
    {
        private long? _positionMs;
        private long? _durationMs;

        public EpisodeCard Progress(long? positionMs, long? durationMs)
        {
            _positionMs = positionMs;
            _durationMs = durationMs;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var art = new List<IUiComponent>
            {
                imageUrl is null ? new Div().Class("episode-card__placeholder") : new MediaImage(imageUrl, title ?? label, 400, 225)
            };
            if (runtimeMinutes is { } minutes)
            {
                art.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes))).Class("episode-card__runtime"));
            }

            if (_positionMs is > 0 and { } position && _durationMs is > 0 and { } duration)
            {
                art.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", Math.Clamp(position, 0, duration).ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progress"]).Class("episode-card__progress"));
            }

            // The link names its action alone: play, or continue at the stored position.
            if (playHref is not null)
            {
                art.Add(new A(resumeAt is null ? new Text("details.play") : new Span(new Text("details.resume"), new ValueText(" " + resumeAt))).Class("episode-card__play")
                    .Attribute("href", playHref));
            }

            var still = new Div([.. art]).Class("episode-card__art");
            new Article(
                still,
                new Div(
                    new Span(new ValueText(label)).Class("episode-card__label"),
                    new H3(new ValueText(title ?? label)).Class("episode-card__title"),
                    overview is null ? new Span() : new P(new ValueText(overview)).Class("episode-card__overview"),
                    new Span(new Text(stateKey)).Class("state-chip state-chip--" + stateKey[(stateKey.LastIndexOf('.') + 1)..].ToLowerInvariant())
                ).Class("episode-card__body")
            ).Class("episode-card").WriteTo(writer, encoder, texts);
        }

        private sealed class Article(params IUiComponent[] children) : HtmlElement("article", children);
    }
}
