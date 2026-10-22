namespace Jularr.Frontend.Framework
{
    // An option with a switch, as the setup's module step shows every choice: its name, one line saying what it does, the switch at the end and, under them,
    // the reason a switch refuses to move (a script fills it). Name and line are one line each and show whole in a tip when clipped.
    public sealed class OptionRow : HtmlElement
    {
        public OptionRow(string name, string? line, IUiComponent control) : base("div", Parts(name, line, control))
        {
            Class("option-row");
        }

        // A tinted card of its own, as the basic functions sit in a grid.
        public OptionRow Card()
        {
            Class("option-row option-row--card");
            return this;
        }

        // A choice under its parent, inside the tinted box below it.
        public OptionRow Sub()
        {
            Class("option-row option-row--sub");
            return this;
        }

        // A media type: the bold name on the line that separates it from the next type.
        public OptionRow Type()
        {
            Class("option-row option-row--type");
            return this;
        }

        public OptionRow Data(string name, string value)
        {
            Attribute("data-" + name, value);
            return this;
        }

        private static IUiComponent[] Parts(string name, string? line, IUiComponent control)
        {
            var text = new List<IUiComponent> { new Span(new Truncated(name)).Class("option-row__name") };
            if (!string.IsNullOrEmpty(line)) text.Add(new Span(new Truncated(line)).Class("option-row__line"));
            return [new Div([.. text]).Class("option-row__text"), new Div(control).Class("option-row__switch"), new P().Class("option-row__reason").Attribute("hidden", "hidden").Attribute("role", "status")];
        }
    }

    // A collapsible group of options: its icon, name and how many of its switches are on, then the options. A script keeps the count as the switches move.
    public sealed class OptionGroup : HtmlElement
    {
        public OptionGroup(IUiComponent icon, string name, string count, bool open, FrontendAssets assets, params IUiComponent[] options)
            : base("details", Parts(icon, name, count, assets, options))
        {
            Class("option-group");
            if (open) Attribute("open", "open");
        }

        private static IUiComponent[] Parts(IUiComponent icon, string name, string count, FrontendAssets assets, IUiComponent[] options) =>
        [
            new Summary(new IconTile(icon, small: true), new Span(new Truncated(name)).Class("option-group__name"), new Tag(new ValueText(count)).Class("tag option-group__count").Attribute("data-option-count", ""),
                new Span(new Icon("chevron-down", assets, 16)).Class("option-group__chevron")).Class("option-group__head"),
            new Div(options).Class("option-group__body")
        ];
    }

    // A starting set of switches as a card: its icon, name and what it gives, marked while the switches are exactly its set.
    public sealed class OptionPreset : HtmlElement
    {
        public OptionPreset(IUiComponent icon, string nameKey, string lineKey, string key) : base("button", new IconTile(icon), new Span(new Text(nameKey)).Class("option-preset__name"),
            new Span(new Text(lineKey)).Class("option-preset__line"))
        {
            Class("option-preset");
            Attribute("type", "button");
            Attribute("data-setup-preset", key);
            Attribute("aria-pressed", "false");
        }
    }
}
