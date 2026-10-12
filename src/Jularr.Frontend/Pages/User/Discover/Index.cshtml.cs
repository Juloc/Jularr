using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Discover
{
    // Page-specific presentation data; not a new Backend service contract.
    // The page handler must eventually supply this from authorized, capability-filtered Backend reads.
    public sealed record DiscoverSection(string? MediaTypeTitleKey, IReadOnlyList<MediaShelf> Shelves);

    public sealed record DiscoverPageContent(
        IReadOnlyList<NavigationTab> MediaTypes,
        IReadOnlyList<NavigationTab> BrowseViews,
        IReadOnlyList<DiscoverSection> Sections,
        IUiComponent? Hero = null,
        IUiComponent? Continuation = null);

    public sealed class IndexModel : PreparedPage
    {
        // No default module set, fabricated provider cards, or mock rankings.
        public DiscoverPageContent? Discovery { get; private set; }

        internal void SetDiscovery(DiscoverPageContent content) =>
            Discovery = content ?? throw new ArgumentNullException(nameof(content));

        protected override void Define(Page page)
        {
            page.Title("discover.title");
            page.Navigation("nav.home");
            // One route owns the composition. Components are reusable building blocks.
            // No UI is manufactured until the authorized Backend supplies visible scopes.
            if (Discovery is not { MediaTypes.Count: > 0 } data)
            {
                return;
            }

            page.Asset("Pages/User/Discover/Index.entry.scss");

            page.Content.Add(new NavigationTabs(data.MediaTypes, "discover.mediaTypes", "media"));

            if (data.BrowseViews.Count > 0)
            {
                page.Content.Add(new NavigationTabs(data.BrowseViews, "discover.browseViews", "browse"));
            }

            if (data.Hero is not null) page.Content.Add(data.Hero);
            if (data.Continuation is not null) page.Content.Add(data.Continuation);

            foreach (var section in data.Sections)
            {
                var visibleShelves = section.Shelves.Where(shelf => shelf.HasItems).ToArray();
                if (visibleShelves.Length == 0) continue;

                if (section.MediaTypeTitleKey is not null)
                {
                    page.Content.Add(new H2(new Text(section.MediaTypeTitleKey))
                        .Class("discover-media-heading"));
                }

                page.Content.AddRange(visibleShelves);
            }
        }
    }
}
