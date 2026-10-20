namespace Jularr.Frontend.Framework
{
    // A form that posts hidden values to a handler of its page, for actions without fields; forms with fields are built from their data contracts.
    public sealed class PostForm : HtmlElement
    {
        public PostForm(string action, string? csrf, IEnumerable<(string Name, string Value)> hidden, params IUiComponent[] children)
            : base("form", [new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", csrf ?? ""),
                .. hidden.Select(item => (IUiComponent)new Input().Attribute("type", "hidden").Attribute("name", item.Name).Attribute("value", item.Value)), .. children])
        {
            Attribute("method", "post");
            Attribute("action", action);
        }
    }
}
