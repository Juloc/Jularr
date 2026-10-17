using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Featured title with backdrop artwork; the page decides which title is featured.
    public sealed class MediaHero(string title, string? backdropUrl, string? meta, string? overview, string? actionKey = null, string? actionHref = null, string? detailsHref = null, string? jxlBackdropUrl = null) : IUiComponent
    {
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

            var body = new List<IUiComponent> { new H2(new ValueText(title)).Class("media-hero__title") };
            if (meta is not null) body.Add(new P(new ValueText(meta)).Class("media-hero__meta"));
            if (overview is not null) body.Add(new P(new ValueText(overview)).Class("media-hero__overview"));
            if (actions.Count > 0) body.Add(new Div([.. actions]).Class("media-hero__actions"));

            var parts = new List<IUiComponent>();
            if (backdropUrl is not null) parts.Add(new MediaImage(backdropUrl, "", 1280, 720, priority: true, jxlUrl: jxlBackdropUrl));
            parts.Add(new Div([.. body]).Class("media-hero__body"));
            new Section([.. parts]).Class(backdropUrl is null ? "media-hero media-hero--placeholder" : "media-hero").WriteTo(writer, encoder, texts);
        }
    }
}
