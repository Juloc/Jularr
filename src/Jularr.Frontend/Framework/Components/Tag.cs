namespace Jularr.Frontend.Framework
{
    public sealed class Tag : HtmlElement
    {
        public Tag(params IUiComponent[] content) : base("span", content)
        {
            Class("tag");
        }

        public Tag Tone(string color)
        {
            if (color is not ("blue" or "pink" or "green" or "yellow" or "purple")) throw new ArgumentException("Unknown tag tone.", nameof(color));
            Class("tag tag--" + color);
            return this;
        }

        public Tag Accent()
        {
            Class("tag tag--accent");
            return this;
        }

        public Tag Neutral()
        {
            Class("tag tag--neutral");
            return this;
        }
    }
}
