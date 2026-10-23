using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed class MediaShelf(FrontendAssets assets, string titleKey, string? browseHref, IReadOnlyList<IUiComponent> cards) : IUiComponent
    {
        public bool HasItems => cards.Count > 0;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!HasItems) return;

            var heading = new List<IUiComponent> { new H2(new Text(titleKey)) };
            if (browseHref is not null)
                heading.Add(new A(new Text("common.seeAll")).Attribute("href", browseHref));
            heading.Add(new Div(
                new Button(new Icon("chevron-left", assets)).Class("button media-shelf__control").Attribute("data-shelf-direction", "previous")
                    .Attribute("aria-label", texts["media.shelfPrevious"]).Attribute("hidden", "hidden"),
                new Button(new Icon("chevron-left", assets)).Class("button media-shelf__control media-shelf__control--next").Attribute("data-shelf-direction", "next")
                    .Attribute("aria-label", texts["media.shelfNext"]).Attribute("hidden", "hidden")
            ).Class("media-shelf__controls"));

            new Section(
                new Div([.. heading]).Class("media-shelf__heading"),
                new Div([.. cards]).Class("media-shelf__items").Attribute("tabindex", "0").Attribute("role", "region").Attribute("aria-label", texts[titleKey])
            ).Class("media-shelf").WriteTo(writer, encoder, texts);
        }
    }
}
