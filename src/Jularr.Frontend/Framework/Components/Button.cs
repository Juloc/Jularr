namespace Jularr.Frontend.Framework
{
    public sealed class Button : HtmlElement
    {
        private string _tone = "";
        private string _shape = "";

        public Button(params IUiComponent[] children) : base("button", children)
        {
            Class("button");
            Attribute("type", "button");
        }

        public Button Primary()
        {
            _tone = " button--primary";
            ApplyStyle();
            return this;
        }

        // A quiet action beside the primary one: the soft accent tint without an outline.
        public Button Soft()
        {
            _tone = " button--soft";
            ApplyStyle();
            return this;
        }

        public Button Compact()
        {
            _shape = " button--compact";
            ApplyStyle();
            return this;
        }

        public Button IconOnly(string label)
        {
            _shape = " button--icon";
            ApplyStyle();
            Attribute("aria-label", label);
            return this;
        }

        // Compose tone and shape.
        private void ApplyStyle() => Class("button" + _tone + _shape);

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
