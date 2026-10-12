using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed class MediaShelf(
        string titleKey, string? browseHref, IReadOnlyList<IUiComponent> cards) : IUiComponent
    {
        public bool HasItems => cards.Count > 0;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!HasItems) return;

            var heading = new List<IUiComponent> { new H2(new Text(titleKey)) };
            if (browseHref is not null)
                heading.Add(new A(new Text("common.seeAll")).Attribute("href", browseHref));

            new Section(
                new Div([.. heading]).Class("media-shelf__heading"),
                new Div([.. cards]).Class("media-shelf__items")
            ).Class("media-shelf").WriteTo(writer, encoder, texts);
        }
    }
}
