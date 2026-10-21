namespace Jularr.Frontend.Framework
{
    // The colour family of a tinted element: the accent, the four status colours, or the quiet field.
    public enum UiTone
    {
        Neutral,
        Accent,
        Info,
        Success,
        Warning,
        Danger
    }

    // An icon on a rounded tile in a status tint (a figure, a list row's kind); the small size sits beside a nested row.
    public sealed class IconTile : HtmlElement
    {
        public IconTile(IUiComponent icon, UiTone tone = UiTone.Accent, bool small = false) : base("span", icon)
        {
            Class("icon-tile icon-tile--" + tone.ToString().ToLowerInvariant() + (small ? " icon-tile--small" : ""));
            Attribute("aria-hidden", "true");
        }
    }
}
