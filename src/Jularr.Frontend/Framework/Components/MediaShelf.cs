using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The address of the shelf's next page, when its page script adds further cards at its end.
    // A ranked shelf (a top 10) sets a big number before each card; a title can be given as text instead of a key.
    public sealed class MediaShelf(FrontendAssets assets, string titleKey, string? browseHref, IReadOnlyList<IUiComponent> cards, string? moreHref = null, bool ranked = false, string? title = null)
        : IUiComponent
    {
        public bool HasItems => cards.Count > 0;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!HasItems) return;

            var name = title ?? texts[titleKey];
            var heading = new List<IUiComponent> { new H2(new ValueText(name)) };
            if (browseHref is not null)
                heading.Add(new A(new Text("common.seeAll")).Attribute("href", browseHref));
            heading.Add(new Div(
                new Button(new Icon("chevron-left", assets)).Class("button media-shelf__control").Attribute("data-shelf-direction", "previous")
                    .Attribute("aria-label", texts["media.shelfPrevious"]).Attribute("hidden", "hidden"),
                new Button(new Icon("chevron-left", assets)).Class("button media-shelf__control media-shelf__control--next").Attribute("data-shelf-direction", "next")
                    .Attribute("aria-label", texts["media.shelfNext"]).Attribute("hidden", "hidden")
            ).Class("media-shelf__controls"));

            var shelf = new Section(
                new Div([.. heading]).Class("media-shelf__heading"),
                new Div([.. ranked ? cards.Select((card, index) => (IUiComponent)new Div(
                    new Span(new ValueText((index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture))).Class("media-shelf__rank-number").Attribute("aria-hidden", "true"), card)
                    .Class("media-shelf__rank")) : cards]).Class("media-shelf__items").Attribute("tabindex", "0").Attribute("role", "region").Attribute("aria-label", name)
            ).Class(ranked ? "media-shelf media-shelf--ranked" : "media-shelf");
            if (moreHref is not null) shelf.Attribute("data-shelf-more", moreHref);
            shelf.WriteTo(writer, encoder, texts);
        }
    }
}
