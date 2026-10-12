namespace Jularr.Frontend.Framework
{
    public sealed class Button : HtmlElement
    {
        public Button(params IUiComponent[] children) : base("button", children)
        {
            Class("button");
            Attribute("type", "button");
        }

        public Button Primary()
        {
            Class("button button--primary");
            return this;
        }

        public Button Submit()
        {
            Attribute("type", "submit");
            return this;
        }

        public Button Disabled()
        {
            Attribute("disabled", "disabled");
            return this;
        }
    }
}
