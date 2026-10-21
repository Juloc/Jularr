namespace Jularr.Frontend.Framework
{
    // The row above an admin list: the page's chips on the left, the list's own tools (a scope menu) on the right; one row at every width.
    public sealed class ListToolbar : HtmlElement
    {
        public ListToolbar(IUiComponent chips, params IUiComponent[] tools) : base("div", tools.Length == 0 ? [chips] : [chips, new Div(tools).Class("list-toolbar__tools")])
        {
            Class("list-toolbar");
        }
    }
}
