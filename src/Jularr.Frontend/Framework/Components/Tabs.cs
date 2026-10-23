using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Server-selected tabs. Links navigate to real GET routes; only the selected
    // pane is rendered, with no JS-only state or duplicated H2 page headings.
    public sealed record Tab(string Key, string Href, IReadOnlyList<IUiComponent> Content, bool Selected);

    public sealed class Tabs : IUiComponent
    {
        public List<Tab> Items { get; } = [];

        public Tabs(Action<Tabs> define) => define(this);

        public void Add(string key, string href, bool selected, Action<List<IUiComponent>> define)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(href);
            var content = new List<IUiComponent>();
            define(content);
            Items.Add(new Tab(key, href, content, selected));
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (Items.Count == 0) return;
            if (Items.Count(x => x.Selected) != 1)
                throw new InvalidOperationException("Exactly one tab must be selected.");

            var selected = Items.Single(item => item.Selected);
            new Div(
                new NavigationTabs(Items.Select(item => new NavigationTab(item.Key, item.Href, item.Selected)).ToArray(), "nav.tabs"),
                new Section([.. selected.Content]).Class("tabs__panel")
            ).Class("tabs").WriteTo(writer, encoder, texts);
        }
    }
}
