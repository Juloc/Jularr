using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The one row of media types of Discover, Library and Favorites: an all capsule, then a capsule per area whose types are chosen one by one.
    // Every link carries the whole selection (the address builder gets the types to choose, none for all of them), so the bar works without script.
    public sealed class MediaTypeBar(FrontendAssets assets, IReadOnlyList<string> listed, IReadOnlyCollection<string> chosen, string labelPrefix, Func<IReadOnlyList<string>, string> address,
        IReadOnlyList<IUiComponent>? tools = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var isAll = chosen.Count == listed.Count;

            // Chosen entries, or none for all of them.
            string Types(IEnumerable<string> slugs)
            {
                var wanted = listed.Where(slugs.Contains).ToList();
                return address(wanted.Count == listed.Count ? [] : wanted);
            }

            var groups = new List<CapsuleGroup>();
            foreach (var area in MediaAreas.All)
            {
                var members = area.Slugs.Where(listed.Contains).ToList();
                if (members.Count == 0) continue;

                var shown = members.Count(chosen.Contains);
                var solo = !isAll && chosen.Count == members.Count && shown == members.Count;
                var state = isAll || shown == 0 ? CapsuleGroupState.Idle : solo ? CapsuleGroupState.Solo : shown < members.Count ? CapsuleGroupState.Partial : CapsuleGroupState.Included;
                groups.Add(new CapsuleGroup(area.LabelKey, area.Icon, Types(solo ? listed : members), state, [.. members.Select(slug =>
                    new CapsuleItem(labelPrefix + slug, Types(isAll ? [slug] : chosen.Contains(slug) ? chosen.Except([slug]) : chosen.Append(slug)), !isAll && chosen.Contains(slug), !isAll && !chosen.Contains(slug)))]));
            }

            new CapsuleBar(assets, "discover.mediaTypes", "discover.all", Types([]), isAll, groups, tools).WriteTo(writer, encoder, texts);
        }
    }
}
