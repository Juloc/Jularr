using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Details
{
    // A catalog title opened before it has its details answers at once: what is here shows as it is, what its details bring holds its place with a
    // placeholder of about its size (its people, its similar titles, its facts), so the page does not jump when its script shows the page with them.
    public sealed partial class DetailsModel
    {
        private bool _detailsPending;
        private bool _detailsUnavailable;

        // The Overview's parts in order, with the places of those its details bring: no block stands for a placeholder of that tab; the facts keep their tab,
        // and only their preview is a placeholder.
        private List<(string TabKey, Block? Block)> PendingParts(WorkDetailsV1 work, IReadOnlyList<Block> blocks)
        {
            var kind = Kind(work);
            var people = kind is WorkKind.Film or WorkKind.Series or WorkKind.Anime;
            var similar = kind is WorkKind.Film or WorkKind.Series;
            var parts = new List<(string TabKey, Block? Block)>();
            foreach (var block in blocks)
            {
                // People or similar titles already here keep their own part.
                if (block.TabKey == "people") people = false;
                if (block.TabKey == "similar") similar = false;
                if (people && block.TabKey is "related" or "similar" or "details" or "editions" or "files")
                {
                    parts.Add(("people", null));
                    people = false;
                }

                if (similar && block.TabKey is "details" or "editions" or "files")
                {
                    parts.Add(("similar", null));
                    similar = false;
                }

                parts.Add((block.TabKey, block));
            }

            return parts;
        }

        // A tab of the row or the placeholder of one.
        private static NavigationTab PendingTab((string TabKey, Block? Block) part, Func<Block, NavigationTab> tab) =>
            part.Block is { } block ? tab(block) : new NavigationTab("", "", false, Placeholder: true);

        // A part of the Overview or the placeholder of one, under the heading it will have.
        private IUiComponent PendingPreview((string TabKey, Block? Block) part)
        {
            if (part.Block is { TabKey: not "details" } block) return Preview(block);

            var (label, body) = part.TabKey switch
            {
                "people" => ("details.tab.people", new Div(
                    new H3(new Span().Class("skeleton-block details-pending details-pending--line")).Class("details-sub"),
                    new Div([.. Enumerable.Range(0, 8).Select(_ => (IUiComponent)new Div(
                        new Div().Class("details-person__photo skeleton-block"),
                        new Span().Class("skeleton-block details-pending details-pending--line"),
                        new Span().Class("skeleton-block details-pending details-pending--line details-pending--short")).Class("details-person"))]).Class("details-people")).Class("details-groups")),
                "similar" => ("details.tab.similar", new Div([.. Enumerable.Range(0, 8).Select(_ => (IUiComponent)new Div(
                    new Div().Class("skeleton-block details-pending--cover"),
                    new Span().Class("skeleton-block details-pending details-pending--line"),
                    new Span().Class("skeleton-block details-pending details-pending--line details-pending--short")).Class("details-pending--card"))]).Class("media-grid")),
                _ => ("details.tab.facts", (IUiComponent)new Div().Class("skeleton-block details-pending--facts"))
            };
            // The heading keeps the room of its link to the tab, which comes with the part.
            return new Section(new Div(new H2(new Text(label)), new Span(new Text("common.seeAll"), new Icon("chevron-right", assets, 14)).Class("details-block__all details-pending--unseen"))
                .Class("details-block__heading"), body).Class("details-block").Attribute("aria-hidden", "true");
        }
    }
}
