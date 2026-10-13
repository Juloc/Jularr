namespace Jularr.Frontend.Framework
{
    public sealed class Tag : HtmlElement
    {
        public Tag(params IUiComponent[] content) : base("span", content)
        {
            Class("tag");
        }

        public Tag Accent()
        {
            Class("tag tag--accent");
            return this;
        }
    }
}
