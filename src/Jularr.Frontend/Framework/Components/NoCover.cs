using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public enum NoCoverKind { Poster, Person }

    // The one stand-in for a poster, backdrop, cover or photo that is missing: the Jularr mark with a question mark (a silhouette for a person),
    // in the soft accent tones of both skins and brightnesses. A broken image is swapped for it by MediaImage.entry.ts from the layout's template.
    public sealed class NoCover(FrontendAssets assets, NoCoverKind kind = NoCoverKind.Poster, string? cssClass = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var icon = new Svg(new Use().Attribute("href", assets.IconHref(kind == NoCoverKind.Person ? "no-person" : "no-cover")))
                .Class("no-cover__icon").Attribute("viewBox", "0 0 48 48").Attribute("focusable", "false");
            new Span(icon)
                .Class("no-cover no-cover--" + (kind == NoCoverKind.Person ? "person" : "poster") + (cssClass is null ? "" : " " + cssClass))
                .Attribute("data-kind", kind == NoCoverKind.Person ? "person" : "poster")
                .Attribute("aria-hidden", "true")
                .WriteTo(writer, encoder, texts);
        }
    }

    // Both stand-ins once per document, for the script that replaces an image that failed to load.
    public sealed class NoCoverTemplate(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Template(new NoCover(assets), new NoCover(assets, NoCoverKind.Person)).Attribute("data-no-cover", "").WriteTo(writer, encoder, texts);
    }
}
