namespace Jularr.Frontend.Framework
{
    public sealed class Tag : HtmlElement
    {
        public Tag(params IUiComponent[] content) : base("span", content)
        {
            Class("tag");
        }

        // A pill in the warning tint, such as a job that waits while Jularr is used.
        public Tag Warning()
        {
            Class("tag tag--warning");
            return this;
        }

        public Tag Accent()
        {
            Class("tag tag--accent");
            return this;
        }
    }
}
