namespace Jularr.Frontend.Framework
{
    // A setting with a switch as a card (the mockups' feature card): an icon tile, its name with the state under it and the switch at the end,
    // and below them the tags that belong to it (what it needs, a missing library). Cards sit on a grid inside the form that saves them.
    public sealed class SettingCard : HtmlElement
    {
        private readonly bool _isOn;

        public SettingCard(IconTile icon, IUiComponent name, IUiComponent control, bool isOn, string stateOnKey, string stateOffKey, IReadOnlyList<IUiComponent>? tags = null)
            : base("div", Parts(icon, name, control, isOn, stateOnKey, stateOffKey, tags))
        {
            _isOn = isOn;
            Class("setting-card" + (isOn ? " setting-card--on" : ""));
        }

        // A data attribute the page's script reads.
        public SettingCard Data(string name, string value)
        {
            Attribute("data-" + name, value);
            return this;
        }

        // The switch cannot move for now; the card's tags say why.
        public SettingCard Blocked()
        {
            Class("setting-card setting-card--blocked" + (_isOn ? " setting-card--on" : ""));
            return this;
        }

        private static IUiComponent[] Parts(IconTile icon, IUiComponent name, IUiComponent control, bool isOn, string stateOnKey, string stateOffKey, IReadOnlyList<IUiComponent>? tags)
        {
            // Both states are written; the switch's own state shows one of them.
            var state = new Span(new Span(new Text(stateOnKey)).Class("setting-card__state-on"), new Span(new Text(stateOffKey)).Class("setting-card__state-off")).Class("setting-card__state");
            var head = new Div(icon, new Div(new Span(name).Class("setting-card__name"), state).Class("setting-card__text"), new Div(control).Class("setting-card__switch")).Class("setting-card__head");
            return tags is { Count: > 0 } ? [head, new Div([.. tags]).Class("setting-card__tags")] : [head];
        }
    }
}
