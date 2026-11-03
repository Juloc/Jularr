using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The one row of media types of Discover, Library and Favorites: an all capsule, then a capsule per area whose types are chosen one by one.
    // Every link carries the whole selection (the address builder gets the types to choose, none for all of them), so the bar works without script.
    // A single-choice bar (a page about one type, such as the quality profiles) has no all capsule: every link names the one type it chooses, a group's the type that is chosen or its first.
    public sealed class MediaTypeBar(FrontendAssets assets, IReadOnlyList<string> listed, IReadOnlyCollection<string> chosen, string labelPrefix, Func<IReadOnlyList<string>, string> address,
        IReadOnlyList<IUiComponent>? tools = null, bool isSingle = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var isAll = !isSingle && chosen.Count == listed.Count;

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
                if (isSingle)
                {
                    groups.Add(new CapsuleGroup(area.LabelKey, area.Icon, address([members.FirstOrDefault(chosen.Contains) ?? members[0]]), shown == 0 ? CapsuleGroupState.Idle : CapsuleGroupState.Solo,
                        [.. members.Select(slug => new CapsuleItem(labelPrefix + slug, address([slug]), chosen.Contains(slug)))]));
                    continue;
                }

                groups.Add(new CapsuleGroup(area.LabelKey, area.Icon, Types(solo ? listed : members), state, [.. members.Select(slug =>
                    new CapsuleItem(labelPrefix + slug, Types(isAll ? [slug] : chosen.Contains(slug) ? chosen.Except([slug]) : chosen.Append(slug)), !isAll && chosen.Contains(slug), !isAll && !chosen.Contains(slug)))]));
            }

            new CapsuleBar(assets, "discover.mediaTypes", "discover.all", Types([]), isAll, groups, tools, !isSingle).WriteTo(writer, encoder, texts);
        }
    }
}
