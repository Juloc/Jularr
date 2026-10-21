using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A GET search; the target page reads the query from the address.
    public sealed class SearchBox(string action, string labelKey, string? value, IReadOnlyDictionary<string, string>? keep = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var id = InputId(labelKey);
            var children = new List<IUiComponent>
            {
                new Label(new Text(labelKey)).Class("visually-hidden").Attribute("for", id),
                new Input().Attribute("type", "search").Attribute("name", "q").Attribute("id", id).Attribute("value", value ?? "")
                    .Attribute("placeholder", texts[labelKey]).Class("search-box__input")
            };

            // Scope fields stay with the query.
            foreach (var (name, scope) in keep ?? new Dictionary<string, string>())
            {
                children.Add(new Input().Attribute("type", "hidden").Attribute("name", name).Attribute("value", scope));
            }

            new SearchForm([.. children]).Class("search-box").Attribute("action", action).Attribute("method", "get").Attribute("role", "search").WriteTo(writer, encoder, texts);
        }

        // Other chrome (the header's icon) points its label at the field.
        public static string InputId(string labelKey) => "search-" + labelKey.Replace('.', '-');

        private sealed class SearchForm(params IUiComponent[] children) : HtmlElement("form", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
