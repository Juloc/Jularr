using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One inert preview per title.
    public sealed class MediaPreview(string id, FrontendAssets assets, string title, string? posterUrl, string? backdropUrl, string? meta, string? overview) : IUiComponent
    {
        private string? _youtubeId;
        private IUiComponent[] _content = [];
        private IUiComponent[] _actions = [];

        public MediaPreview Trailer(string youtubeId)
        {
            if (youtubeId.Length != 11 || youtubeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new ArgumentException("Invalid YouTube video identifier.", nameof(youtubeId));

            _youtubeId = youtubeId;
            return this;
        }

        public MediaPreview Content(params IUiComponent[] content)
        {
            _content = content;
            return this;
        }

        public MediaPreview Actions(params IUiComponent[] actions)
        {
            _actions = actions;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Artwork, never eager playback.
            var hero = new List<IUiComponent>();
            if (backdropUrl is not null) hero.Add(new MediaImage(backdropUrl, "", 1280, 720));
            if (_youtubeId is not null)
            {
                hero.Add(new Button(new Icon("player-play", assets)).Class("button media-preview__play")
                    .Attribute("aria-label", texts["media.preview.playTrailer"]).Attribute("data-trailer-id", _youtubeId));
            }
            else hero.Add(new P(new Text("media.preview.noTrailer")).Class("media-preview__unavailable"));

            var body = new List<IUiComponent>
            {
                new H2(new ValueText(title)).Attribute("id", id + "-title"),
                meta is null ? new Span() : new P(new ValueText(meta)).Class("media-preview__meta"),
                overview is null ? new Span() : new P(new ValueText(overview)).Class("media-preview__overview")
            };
            body.AddRange(_content);
            if (_youtubeId is not null)
                body.Add(new P(new A(new Text("media.preview.externalTrailer")).Attribute("href", "https://www.youtube.com/watch?v=" + _youtubeId)
                    .Attribute("target", "_blank").Attribute("rel", "noopener")).Class("media-preview__source"));
            body.Add(new Div(_actions).Class("media-preview__actions"));

            // Encoded, server-rendered content.
            new Template(new Section(
                new Div([.. hero]).Class("media-preview__hero"),
                new Div(
                    posterUrl is null ? new Span() : new Div(new MediaImage(posterUrl, title, 342, 513)).Class("media-preview__poster"),
                    new Div([.. body]).Class("media-preview__body")
                ).Class("media-preview__identity")
            ).Class("media-preview__content")).Attribute("id", id).WriteTo(writer, encoder, texts);
        }

        public static IUiComponent Host(FrontendAssets assets, UiText texts) => new Dialog(
            new Div(new Button(new Icon("close", assets)).Class("button media-preview__close")
                .Attribute("data-preview-close", "").Attribute("aria-label", texts["media.preview.close"]).Attribute("autofocus", "autofocus"))
                .Class("media-preview__toolbar"),
            new Div().Attribute("data-preview-content", "")
        ).Class("media-preview").Attribute("data-media-preview", "").Attribute("aria-label", texts["media.preview.title"]);

        private sealed class Template(params IUiComponent[] content) : HtmlElement("template", content);
        private sealed class Dialog(params IUiComponent[] content) : HtmlElement("dialog", content);
    }
}
