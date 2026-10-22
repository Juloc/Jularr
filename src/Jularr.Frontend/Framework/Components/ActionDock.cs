namespace Jularr.Frontend.Framework
{
    // The dock of an admin list page: one floating pill, centred above the bottom edge like the save pill, with three zones as slots.
    // Actions (the page's or table's buttons), bulk (shown instead of the actions while rows with data-dock-select are chosen, with their count) and the pager.
    // The pager is never wired by a page: a list that is `.Paged()` puts its controls here (see Paging.cs). When the page also has a save pill, the dock stacks above it.
    public sealed class ActionDock : HtmlElement
    {
        public ActionDock(FrontendAssets assets, string ariaLabelKey, IReadOnlyList<IUiComponent>? actions = null, IReadOnlyList<IUiComponent>? bulk = null)
            : base("div", Zones(actions, bulk))
        {
            Class("floating-pill action-dock");
            Attribute("role", "region");
            TextAttribute("aria-label", ariaLabelKey);
        }

        private ActionDock(IUiComponent[] zones) : base("div", zones)
        {
        }

        // The form-page dock (the save pill) is not where a list's pager goes.
        public bool IsForm { get; private init; }

        // The form-page mode: what the page's forms changed ("3 changes"), Discard and Save in the same floating pill. The page script
        // (SavePill.ts) shows it while a value differs from the loaded page and saves every changed form in turn.
        public static ActionDock ForForm() => (ActionDock)new ActionDock([
            new Div(new Span().Class("save-pill__count action-dock__count").Attribute("aria-live", "polite")
                    .TextAttribute("data-one", "savePill.one").TextAttribute("data-other", "savePill.other")
                    .TextAttribute("data-failed", "savePill.failed").TextAttribute("data-leave", "savePill.leave"),
                new Span().Class("action-dock__divider").Attribute("aria-hidden", "true"),
                new Button(new Text("savePill.discard")).Attribute("data-save-discard", ""),
                new Button(new Text("common.save")).Primary().Attribute("data-save-submit", "").TextAttribute("data-saved", "savePill.saved")).Class("action-dock__zone action-dock__form")])
            { IsForm = true }
            .Class("floating-pill action-dock action-dock--form save-pill").Attribute("data-save-pill", "").TextAttribute("aria-label", "savePill.region")
            .Attribute("role", "region").Attribute("hidden", "hidden");

        private static IUiComponent[] Zones(IReadOnlyList<IUiComponent>? actions, IReadOnlyList<IUiComponent>? bulk)
        {
            var zones = new List<IUiComponent>();
            if (actions is { Count: > 0 }) zones.Add(new Div([.. actions]).Class("action-dock__zone action-dock__actions"));
            if (bulk is { Count: > 0 })
            {
                zones.Add(new Div([new Span().Class("action-dock__count").Attribute("data-dock-count", "").TextAttribute("data-format", "dock.selected").Attribute("aria-live", "polite"), .. bulk])
                    .Class("action-dock__zone action-dock__bulk").Attribute("hidden", "hidden"));
            }

            return [.. zones.SelectMany((zone, index) => index == 0 ? new[] { zone } : new IUiComponent[] { Divider(), zone })];
        }

        private static IUiComponent Divider() => new Span().Class("action-dock__divider").Attribute("aria-hidden", "true");

        // The page's paged list puts its controls last in the dock.
        internal void AddPaging(FrontendAssets assets, DockPaging paging)
        {
            if (ChildCount > 0) AddChild(Divider());
            AddChild(PagingZone(assets, paging));
        }

        // Previous, "Page 2" and Next. A list that is not the page's primary one draws the same pager in place under itself.
        public static IUiComponent PagingZone(FrontendAssets assets, DockPaging paging, bool inline = false) =>
            new Nav(Step(assets, "chevron-left", "common.previous", paging.PreviousHref), new Span(paging.Label).Class("action-dock__label"), Step(assets, "chevron-right", "common.next", paging.NextHref))
                .Class("action-dock__zone action-dock__paging" + (inline ? " action-dock__paging--inline" : "")).TextAttribute("aria-label", "dock.paging");

        // A step is a link when there is a page to go to and a disabled button otherwise.
        private static IUiComponent Step(FrontendAssets assets, string icon, string labelKey, string? href) => href is null
            ? new Button(new Icon(icon, assets, 18)).Disabled().Class("button button--icon").TextAttribute("aria-label", labelKey)
            : new A(new Icon(icon, assets, 18), new Span(new Text(labelKey)).Class("visually-hidden")).Class("button button--icon").Attribute("href", href);
    }
}
