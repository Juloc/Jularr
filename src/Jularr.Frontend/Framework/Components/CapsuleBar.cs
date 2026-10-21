using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Checked: part of the selection; Dimmed: left out while others are chosen.
    public sealed record CapsuleItem(string LabelKey, string Href, bool Checked, bool Dimmed = false);

    // Idle: everything is shown; Included: some of its items are shown; Partial: not all of them; Solo: only this group is shown.
    public enum CapsuleGroupState { Idle, Included, Partial, Solo }

    public sealed record CapsuleGroup(string LabelKey, string Icon, string Href, CapsuleGroupState State, IReadOnlyList<CapsuleItem> Items);

    // One row of capsules: an all capsule, then a capsule per group whose master link selects the group and whose items add or remove one entry.
    // Links carry the whole selection, so the bar works without script; the tools sit at the end.
    public sealed class CapsuleBar(FrontendAssets assets, string ariaLabelKey, string allLabelKey, string allHref, bool allSelected, IReadOnlyList<CapsuleGroup> groups,
        IReadOnlyList<IUiComponent>? tools = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var all = new A(new Icon("grid", assets, 16), new Span(new Text(allLabelKey))).Class(allSelected ? "capsule-bar__all capsule-bar__all--on" : "capsule-bar__all")
                .Attribute("href", allHref).Attribute("aria-label", texts[allLabelKey]);
            if (allSelected) all.Attribute("aria-current", "true");
            var capsules = new List<IUiComponent> { all };

            foreach (var group in groups)
            {
                var master = new A(new Icon(group.Icon, assets, 16), new Span(new Text(group.LabelKey))).Attribute("href", group.Href).Attribute("aria-label", texts[group.LabelKey])
                    .Attribute("title", texts[group.LabelKey]).Class(group.State switch
                    {
                        CapsuleGroupState.Solo => "capsule-group__master capsule-group__master--solo",
                        CapsuleGroupState.Partial => "capsule-group__master capsule-group__master--included capsule-group__master--partial",
                        CapsuleGroupState.Included => "capsule-group__master capsule-group__master--included",
                        _ => "capsule-group__master"
                    });
                if (group.State == CapsuleGroupState.Solo) master.Attribute("aria-current", "true");

                var parts = new List<IUiComponent> { master, new Span().Class("capsule-group__divider").Attribute("aria-hidden", "true") };
                parts.AddRange(group.Items.Select(item =>
                {
                    var link = new A(new Text(item.LabelKey)).Attribute("href", item.Href)
                        .Class(item.Checked ? "capsule-group__item capsule-group__item--checked" : item.Dimmed ? "capsule-group__item capsule-group__item--dimmed" : "capsule-group__item");
                    return (IUiComponent)(item.Checked ? link.Attribute("aria-current", "true") : link);
                }));
                capsules.Add(new Div([.. parts]).Class("capsule-group").Attribute("role", "group").Attribute("aria-label", texts[group.LabelKey]));
            }

            var bar = new List<IUiComponent> { new Nav([.. capsules]).Class("capsule-bar__areas").Attribute("aria-label", texts[ariaLabelKey]) };
            if (tools is { Count: > 0 }) bar.Add(new Div([.. tools]).Class("capsule-bar__tools"));
            new Div(new Div([.. bar]).Class("capsule-bar__row")).Class("capsule-bar").WriteTo(writer, encoder, texts);
        }
    }
}
