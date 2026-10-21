namespace Jularr.Frontend.Framework
{
    // The part of a page that an action inside it updates on its own: a form in a region posts, and only this region of the answer replaces the
    // region on the page (ActionSwap.ts). Open sections, scroll position and focus stay. A form outside every region updates the page content instead.
    public sealed class Region : HtmlElement
    {
        public Region(string id, params IUiComponent[] children) : base("div", children)
        {
            Attribute("id", id);
            Attribute("data-region", "");
        }
    }
}
