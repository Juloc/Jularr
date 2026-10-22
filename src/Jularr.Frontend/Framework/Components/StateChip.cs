namespace Jularr.Frontend.Framework
{
    // A state as a pill in the tone of its meaning. With an icon the icon replaces the dot, so the state never rests on colour alone; the spinning variant shows work in progress.
    // The label stays on one line and ends in an ellipsis where it has no room.
    public sealed class StateChip : HtmlElement
    {
        public StateChip(IUiComponent label, UiTone tone, IUiComponent? icon = null, bool spinning = false) : base("span", icon is null ? [new Span(label).Class("state-chip__label")] : [new Span(icon).Class("state-chip__icon"), new Span(label).Class("state-chip__label")])
        {
            Class("state-chip state-chip--" + tone.ToString().ToLowerInvariant() + (icon is null ? "" : " state-chip--icon") + (spinning ? " state-chip--spinning" : ""));
        }
    }
}
