using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A GET search; the target page reads the query from the address. With a list selector it also narrows that list as the person types (ListFilter), and an icon makes it a field of a page's toolbar.
    public sealed class SearchBox(string action, string labelKey, string? value, IReadOnlyDictionary<string, string>? keep = null, string? listFilter = null, IUiComponent? icon = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var id = InputId(labelKey);
            var field = new Input().Attribute("type", "search").Attribute("name", "q").Attribute("id", id).Attribute("value", value ?? "")
                .Attribute("placeholder", texts[labelKey]).Class("search-box__input");
            if (listFilter is not null) field.Attribute("data-list-filter", listFilter);
            var children = new List<IUiComponent> { new Label(new Text(labelKey)).Class("visually-hidden").Attribute("for", id) };
            if (icon is not null) children.Add(new Span(icon).Class("search-box__icon"));
            children.Add(field);

            // Scope fields stay with the query.
            foreach (var (name, scope) in keep ?? new Dictionary<string, string>())
            {
                children.Add(new Input().Attribute("type", "hidden").Attribute("name", name).Attribute("value", scope));
            }

            new SearchForm([.. children]).Class(icon is null ? "search-box" : "search-box search-box--inline").Attribute("action", action).Attribute("method", "get").Attribute("role", "search").WriteTo(writer, encoder, texts);
        }

        // Other chrome (the header's icon) points its label at the field.
        public static string InputId(string labelKey) => "search-" + labelKey.Replace('.', '-');

        private sealed class SearchForm(params IUiComponent[] children) : HtmlElement("form", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
