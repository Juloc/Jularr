using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One episode: its still with its number or length and progress, its title, and its state; a playable one opens the player.
    // A page lists episodes as cards or, with Row(), as lines (the still, the title with one line of facts, the state and the actions);
    // the player's list uses Compact().
    public sealed class EpisodeCard(string label, string? title, string? imageUrl, int? runtimeMinutes, string stateKey, string? playHref, string? resumeAt = null) : IUiComponent
    {
        private long? _positionMs;
        private long? _durationMs;
        private bool _compact;
        private bool _current;
        private bool _row;
        private string? _facts;
        private IUiComponent? _state;
        private IUiComponent? _actions;

        public EpisodeCard Compact(bool current = false)
        {
            _compact = true;
            _current = current;
            return this;
        }

        // A line of a list instead of a card.
        public EpisodeCard Row()
        {
            _row = true;
            return this;
        }

        // The small line under the title (length and day) and the state the page names itself.
        public EpisodeCard Facts(string? facts, IUiComponent state)
        {
            _facts = facts;
            _state = state;
            return this;
        }

        // The buttons that change the profile's state of the episode, at the end of its line.
        public EpisodeCard Actions(IUiComponent? actions)
        {
            _actions = actions;
            return this;
        }

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
                imageUrl is null ? new Div().Class("episode-card__placeholder") : new MediaImage(imageUrl, label, 400, 225)
            };

            // The player's list shows the length over the still, a page's list the number.
            if (_compact && runtimeMinutes is { } minutes)
            {
                art.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes))).Class("episode-card__runtime"));
            }
            else if (!_compact) art.Add(new Span(new ValueText(label)).Class("episode-card__index"));

            if ((_positionMs is > 0 || _current) && _durationMs is > 0 and { } duration)
            {
                art.Add(new Progress().Attribute("max", duration.ToString(CultureInfo.InvariantCulture))
                    .Attribute("value", Math.Clamp(_positionMs ?? 0, 0, duration).ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", texts["media.progress"]).Class("episode-card__progress"));
            }

            // The link names its action alone: play, or continue at the stored position.
            if (playHref is not null)
            {
                art.Add(new A(resumeAt is null ? new Text("details.play") : new Span(new Text("details.resume"), new ValueText(" " + resumeAt))).Class("episode-card__play")
                    .Attribute("href", playHref));
            }

            var still = new Div([.. art]).Class("episode-card__art");
            var state = _state ?? new Span(new Text(stateKey)).Class("state-chip state-chip--" + stateKey[(stateKey.LastIndexOf('.') + 1)..].ToLowerInvariant());
            var text = new List<IUiComponent>();
            if (_compact)
            {
                text.Add(new Span(new ValueText(label)).Class("episode-card__label"));
                if (title is not null) text.Add(new H3(new ValueText(title)).Class("episode-card__title"));
            }
            else
            {
                text.Add(new H3(new Truncated(title ?? label)).Class("episode-card__title"));
                if (_facts is not null) text.Add(new P(new Truncated(_facts)).Class("episode-card__facts"));
            }

            var foot = new List<IUiComponent> { state };
            if (_actions is not null) foot.Add(new Div(_actions).Class("episode-card__actions"));
            var card = new Article(still, new Div(new Div([.. text]).Class("episode-card__text"), new Div([.. foot]).Class("episode-card__foot")).Class("episode-card__body"))
                .Class(_compact ? "episode-card episode-card--compact" : _row ? "episode-card episode-card--list" : "episode-card");
            if (_current) card.Attribute("aria-current", "true");
            card.WriteTo(writer, encoder, texts);
        }

        private sealed class Article(params IUiComponent[] children) : HtmlElement("article", children);
    }
}
