namespace Jularr.Frontend.Framework
{
    // A switch that submits its form: it shows the state and the click is the change. Its styles (`work-switch`) load with the shell.
    public sealed class SwitchButton : HtmlElement
    {
        public SwitchButton(bool isOn, string actionKey, string? labelKey = null) : base("button", [new Span().Class("work-switch__track"), labelKey is null ? new Span() : new Span(new Text(labelKey)).Class("work-switch__label")])
        {
            Attribute("type", "submit");
            Attribute("role", "switch");
            Attribute("aria-checked", isOn ? "true" : "false");
            TextAttribute("aria-label", actionKey);
            Class("work-switch" + (isOn ? " work-switch--on" : ""));
        }
    }
}
