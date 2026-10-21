using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The filter drawer of Library and Favorites: the media types by area, then the release year, in one GET form so that the address holds the choice.
    // The page's other address values travel as hidden fields; a form for several panes is the page's own.
    public sealed class MediaFilterDrawer(FrontendAssets assets, string id, string titleKey, string formAddress, ScopeSelection scopes, string? year, string resetHref,
        IReadOnlyDictionary<string, string> keep) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var chosen = scopes.ChosenSlugs.ToHashSet();
            var body = new List<IUiComponent>();
            foreach (var area in MediaAreas.All)
            {
                var members = area.Slugs.Where(slug => scopes.Listed.Any(scope => scope.Slug == slug)).ToList();
                if (members.Count > 0) body.Add(new ChoiceGroup(assets, area.LabelKey, "type", [.. members.Select(slug => new ChoicePick(slug, chosen.Contains(slug), "library.scope." + slug))], new ChoiceOptions(Multiple: true)));
            }

            body.Add(new ChoiceGroup(assets, "discover.filter.year", "year", [new("", year is null, "discover.all"), new("2020", year == "2020", "discover.year.since2020"), new("old", year == "old", "discover.year.before2020")]));
            body.AddRange(keep.Select(item => (IUiComponent)new Input().Attribute("type", "hidden").Attribute("name", item.Key).Attribute("value", item.Value)));

            new Drawer(assets, id, [new DrawerPane("main", titleKey, titleKey, "filter", body,
            [
                new A(new Text("discover.reset")).Class("button").Attribute("href", resetHref),
                new Button(new Text("common.apply")).Primary().Submit()
            ], formAddress)]).WriteTo(writer, encoder, texts);
        }
    }
}
