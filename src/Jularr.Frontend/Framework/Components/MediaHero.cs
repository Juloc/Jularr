using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Featured title with backdrop artwork; the page decides which title is featured.
    // A page whose breadcrumb already ends with this title keeps the heading for assistive technology only.
    // With a poster, the hero has a plain cover beside top-aligned details and a bottom actions/progress row.
    public sealed class MediaHero(string title, string? backdropUrl, string? meta, string? overview, string? actionKey = null, string? actionHref = null, string? detailsHref = null,
        bool titleInBreadcrumb = false) : IUiComponent
    {
        private string? _posterUrl;
        private bool _hasPoster;
        private IUiComponent? _placeholder;
        private IUiComponent[] _chips = [];
        private IUiComponent[] _actions = [];
        private IUiComponent? _corner;
        private IUiComponent? _rating;
        private (int Percent, string Label, string? Value)? _progress;

        // A plain cover; a title without art shows the type icon.
        public MediaHero Poster(string? posterUrl, IUiComponent? placeholder = null)
        {
            _hasPoster = true;
            _posterUrl = posterUrl;
            _placeholder = placeholder;
            return this;
        }

        public MediaHero Chips(params IUiComponent[] chips)
        {
            _chips = chips;
            return this;
        }

        // The catalog's rating on a scale of ten with the catalog that gave it.
        public MediaHero Rating(IUiComponent rating)
        {
            _rating = rating;
            return this;
        }

        public MediaHero Actions(params IUiComponent[] actions)
        {
            _actions = actions;
            return this;
        }

        // A small control in the top right corner.
        public MediaHero Corner(IUiComponent corner)
        {
            _corner = corner;
            return this;
        }

        // Where the profile stands, a bar at the bottom right with a word and a figure above it.
        public MediaHero Progress(int percent, string label, string? value = null)
        {
            _progress = (Math.Clamp(percent, 0, 100), label, value);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var actions = new List<IUiComponent>();
            if (actionKey is not null && actionHref is not null)
            {
                actions.Add(new A(new Text(actionKey)).Class("button button--primary").Attribute("href", actionHref));
            }

            if (detailsHref is not null)
            {
                actions.Add(new A(new Text("media.details")).Class("button").Attribute("href", detailsHref));
            }

            actions.AddRange(_actions);

            var body = new List<IUiComponent>();
            body.Add(new H2(new ValueText(title)).Class(titleInBreadcrumb ? "media-hero__title visually-hidden" : "media-hero__title"));
            if (_chips.Length > 0) body.Add(new Div(_chips).Class("media-hero__chips"));
            if (meta is not null || _rating is not null)
            {
                var facts = new List<IUiComponent>();
                if (meta is not null) facts.Add(_hasPoster ? new Span(new Truncated(meta)).Class("media-hero__facts") : new ValueText(meta));
                if (_rating is not null) facts.Add(_rating);
                body.Add(_hasPoster ? new Div([.. facts]).Class("media-hero__meta") : new P([.. facts]).Class("media-hero__meta"));
            }

            if (overview is not null)
            {
                body.Add(_hasPoster
                    ? new P(new Truncated(overview)).Class("media-hero__overview").Attribute("data-hero-summary", "true").Attribute("role", "button").Attribute("tabindex", "0").Attribute("aria-expanded", "false")
                    : new P(new ValueText(overview)).Class("media-hero__overview"));
            }

            if (!_hasPoster && actions.Count > 0) body.Add(new Div([.. actions]).Class("media-hero__actions"));

            var parts = new List<IUiComponent>();
            if (!_hasPoster)
            {
                if (backdropUrl is not null) parts.Add(new MediaImage(backdropUrl, "", 1280, 720, priority: true));
                parts.Add(new Div([.. body]).Class("media-hero__body"));
                new Section([.. parts]).Class(backdropUrl is null ? "media-hero media-hero--placeholder" : "media-hero").WriteTo(writer, encoder, texts);
                return;
            }

            // The backdrop the title has, else a calm plain background; it alone is clipped, so menus of the actions may reach beyond the hero.
            parts.Add(backdropUrl is not null ? new Div(new MediaImage(backdropUrl, "", 1280, 720, priority: true)).Class("media-hero__backdrop")
                : new Div().Class("media-hero__backdrop"));
            if (_corner is not null) parts.Add(new Div(_corner).Class("media-hero__corner"));
            var poster = _posterUrl is null ? new Div(_placeholder ?? new Span()).Class("media-hero__placeholder") : (IUiComponent)new MediaImage(_posterUrl, "", 342, 513, priority: true);
            var footer = new List<IUiComponent>();
            if (actions.Count > 0) footer.Add(new Div([.. actions]).Class("media-hero__actions"));
            if (_progress is { } progress)
            {
                var caption = new List<IUiComponent> { new Span(new ValueText(progress.Label)) };
                if (progress.Value is not null) caption.Add(new Span(new ValueText(progress.Value)));
                footer.Add(new Div(new Div([.. caption]).Class("media-hero__progress-caption"),
                    new Progress().Attribute("max", "100").Attribute("value", progress.Percent.ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", progress.Label).Class("media-hero__progress-bar"))
                    .Class("media-hero__progress"));
            }

            var content = new List<IUiComponent> { new Div([.. body]).Class("media-hero__body") };
            if (footer.Count > 0)
            {
                content.Add(new Div([.. footer]).Class(_progress is not null && actions.Count > 0 ? "media-hero__footer media-hero__footer--progress" : "media-hero__footer"));
            }

            parts.Add(new Div(new Div(poster).Class("media-hero__poster"), new Div([.. content]).Class("media-hero__content")).Class("media-hero__layout"));
            new Section([.. parts]).Class("media-hero media-hero--poster").WriteTo(writer, encoder, texts);
        }
    }
}
