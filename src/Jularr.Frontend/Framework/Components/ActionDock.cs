namespace Jularr.Frontend.Framework
{
    // The paging zone of an admin list: plain GET links (so it works without script and keeps the filters in the address), a label such as "Page 2 / 9" and an optional page size select.
    public sealed record DockPaging(string? PreviousHref, string? NextHref, IUiComponent Label, IUiComponent? PageSize = null);

    // The dock of an admin list page: one floating pill, centred above the bottom edge like the save pill, with three zones as slots.
    // Actions (the page's or table's buttons), bulk (shown instead of the actions while rows with data-dock-select are chosen, with their count) and paging.
    // Admin pages page their lists; they never scroll without end. When the page also has a save pill, the dock stacks above it.
    public sealed class ActionDock : HtmlElement
    {
        public ActionDock(FrontendAssets assets, string ariaLabelKey, IReadOnlyList<IUiComponent>? actions = null, IReadOnlyList<IUiComponent>? bulk = null, DockPaging? paging = null)
            : base("div", Zones(assets, actions, bulk, paging))
        {
            Class("floating-pill action-dock");
            Attribute("role", "region");
            TextAttribute("aria-label", ariaLabelKey);
        }

        private ActionDock(IUiComponent[] zones) : base("div", zones)
        {
        }

        // The form-page mode: what the page's forms changed ("3 changes"), Discard and Save in the same floating pill. The page script
        // (SavePill.ts) shows it while a value differs from the loaded page and saves every changed form in turn.
        public static ActionDock ForForm() => (ActionDock)new ActionDock([
            new Div(new Span().Class("save-pill__count action-dock__count").Attribute("aria-live", "polite")
                    .TextAttribute("data-one", "savePill.one").TextAttribute("data-other", "savePill.other")
                    .TextAttribute("data-failed", "savePill.failed").TextAttribute("data-leave", "savePill.leave"),
                new Span().Class("action-dock__divider").Attribute("aria-hidden", "true"),
                new Button(new Text("savePill.discard")).Attribute("data-save-discard", ""),
                new Button(new Text("common.save")).Primary().Attribute("data-save-submit", "").TextAttribute("data-saved", "savePill.saved")).Class("action-dock__zone action-dock__form")])
            .Class("floating-pill action-dock action-dock--form save-pill").Attribute("data-save-pill", "").TextAttribute("aria-label", "savePill.region")
            .Attribute("role", "region").Attribute("hidden", "hidden");

        private static IUiComponent[] Zones(FrontendAssets assets, IReadOnlyList<IUiComponent>? actions, IReadOnlyList<IUiComponent>? bulk, DockPaging? paging)
        {
            var zones = new List<IUiComponent>();
            if (actions is { Count: > 0 }) zones.Add(new Div([.. actions]).Class("action-dock__zone action-dock__actions"));
            if (bulk is { Count: > 0 })
            {
                zones.Add(new Div([new Span().Class("action-dock__count").Attribute("data-dock-count", "").TextAttribute("data-format", "dock.selected").Attribute("aria-live", "polite"), .. bulk])
                    .Class("action-dock__zone action-dock__bulk").Attribute("hidden", "hidden"));
            }

            if (paging is not null)
            {
                var steps = new List<IUiComponent> { Step(assets, "chevron-left", "common.previous", paging.PreviousHref), new Span(paging.Label).Class("action-dock__label"), Step(assets, "chevron-right", "common.next", paging.NextHref) };
                if (paging.PageSize is not null) steps.Add(paging.PageSize);
                zones.Add(new Nav([.. steps]).Class("action-dock__zone action-dock__paging").TextAttribute("aria-label", "dock.paging"));
            }

            return [.. zones.SelectMany((zone, index) => index == 0 ? new[] { zone } : new IUiComponent[] { new Span().Class("action-dock__divider").Attribute("aria-hidden", "true"), zone })];
        }

        // A step is a link when there is a page to go to and a disabled button otherwise.
        private static IUiComponent Step(FrontendAssets assets, string icon, string labelKey, string? href) => href is null
            ? new Button(new Icon(icon, assets, 18)).Disabled().Class("button button--icon").TextAttribute("aria-label", labelKey)
            : new A(new Icon(icon, assets, 18), new Span(new Text(labelKey)).Class("visually-hidden")).Class("button button--icon").Attribute("href", href);
    }
}
