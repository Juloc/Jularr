using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// A reusable media shelf, not an entire page or a discovery provider.
public sealed class MediaShelf(
    string titleKey, string? browseHref, IReadOnlyList<IUiComponent> cards) : IUiComponent
{
    public bool HasItems => cards.Count > 0;

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        if (!HasItems) return;

        var heading = new List<IUiComponent> { new H2(new Text(titleKey)) };
        if (browseHref is not null)
        {
            heading.Add(new A(new Text("common.seeAll")).Attribute("href", browseHref));
        }

        new Section(
            new Div([.. heading]).Class("media-shelf-heading"),
            new Div([.. cards]).Class("media-rail-items")
        ).Class("media-rail").WriteTo(writer, encoder, texts);
    }
}
