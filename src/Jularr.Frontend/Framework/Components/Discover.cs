using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// Presentation-only structures. The Backend must supply a capability-filtered,
// ordered set of scopes, truthful browse views and actual card components.
public sealed record DiscoverScope(string LabelKey, string Href, bool Selected);

public sealed record DiscoverBrowseView(string LabelKey, string Href, bool Selected);

public sealed record DiscoverShelf(string TitleKey, string? BrowseHref,
    IReadOnlyList<IUiComponent> Cards);

public sealed record DiscoverMediaSection(string? TypeLabelKey,
    IReadOnlyList<DiscoverShelf> Shelves);

public sealed class Discover : IUiComponent
{
    public IReadOnlyList<DiscoverScope> VisibleScopes { get; }
    public IReadOnlyList<DiscoverBrowseView> BrowseViews { get; }
    public IReadOnlyList<DiscoverMediaSection> Sections { get; }
    public IUiComponent? Hero { get; }
    public IUiComponent? Continue { get; }

    public Discover(
        IReadOnlyList<DiscoverScope> visibleScopes,
        IReadOnlyList<DiscoverBrowseView> browseViews,
        IReadOnlyList<DiscoverMediaSection> sections,
        IUiComponent? hero = null,
        IUiComponent? continuation = null)
    {
        VisibleScopes = visibleScopes;
        BrowseViews = browseViews;
        Sections = sections;
        Hero = hero;
        Continue = continuation;
    }

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        if (VisibleScopes.Count == 0)
        {
            return;
        }

        WriteNavigation(writer, encoder, texts, VisibleScopes.Select(scope =>
            (scope.LabelKey, scope.Href, scope.Selected)), "discover.mediaTypes", "discover-scopes");

        if (BrowseViews.Count > 0)
        {
            WriteNavigation(writer, encoder, texts, BrowseViews.Select(view =>
                (view.LabelKey, view.Href, view.Selected)), "discover.browseViews", "discover-browse");
        }

        Hero?.WriteTo(writer, encoder, texts);
        Continue?.WriteTo(writer, encoder, texts);

        foreach (var section in Sections)
        {
            if (!section.Shelves.Any(shelf => shelf.Cards.Count > 0))
            {
                continue;
            }

            if (section.TypeLabelKey is not null)
            {
                new H2(new Text(section.TypeLabelKey))
                    .Class("discover-media-heading")
                    .WriteTo(writer, encoder, texts);
            }

            foreach (var shelf in section.Shelves)
            {
                if (shelf.Cards.Count == 0)
                {
                    continue;
                }

                var header = new List<IUiComponent> { new H2(new Text(shelf.TitleKey)) };
                if (shelf.BrowseHref is not null)
                {
                    header.Add(new A(new Text("discover.seeAll"))
                        .Attribute("href", shelf.BrowseHref));
                }

                new Section(
                    new Div([.. header]).Class("discover-shelf-heading"),
                    new Div([.. shelf.Cards]).Class("media-rail-items")
                ).Class("media-rail").WriteTo(writer, encoder, texts);
            }
        }
    }

    private static void WriteNavigation(TextWriter writer, HtmlEncoder encoder, UiText texts,
        IEnumerable<(string LabelKey, string Href, bool Selected)> items,
        string ariaLabelKey, string className)
    {
        var links = items.Select(item =>
        {
            var link = new A(new Text(item.LabelKey)).Attribute("href", item.Href);
            if (item.Selected)
            {
                link.Attribute("aria-current", "page").Class("is-active");
            }

            return (IUiComponent)link;
        }).ToArray();

        new Nav(links).Class(className)
            .Attribute("aria-label", texts[ariaLabelKey])
            .WriteTo(writer, encoder, texts);
    }
}
