namespace Jularr.Frontend.Framework
{
    // The tone a state, an icon tile or a figure takes.
    public enum UiTone
    {
        Neutral,
        Accent,
        Info,
        Success,
        Warning,
        Danger
    }

    // A state as a pill in the tone of its meaning. With an icon the icon replaces the dot, so the state never rests on colour alone; the spinning variant shows work in progress.
    public sealed class StateChip : HtmlElement
    {
        public StateChip(IUiComponent label, UiTone tone, IUiComponent? icon = null, bool spinning = false) : base("span", icon is null ? [label] : [new Span(icon).Class("state-chip__icon"), label])
        {
            Class("state-chip state-chip--" + tone.ToString().ToLowerInvariant() + (icon is null ? "" : " state-chip--icon") + (spinning ? " state-chip--spinning" : ""));
        }
    }
}
