using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A count shows after the label as a quiet number (a filter row of a list); an icon shows before it (the state a filter chip stands for).
    public sealed record NavigationTab(string LabelKey, string Href, bool Selected, int? Count = null, IUiComponent? Icon = null);

    // A reusable component, not a page or its navigation policy.
    public sealed class NavigationTabs(IReadOnlyList<NavigationTab> tabs, string ariaLabelKey, string? modifier = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (tabs.Count == 0) return;

            var links = tabs.Select(tab =>
            {
                var parts = new List<IUiComponent>();
                if (tab.Icon is not null) parts.Add(new Span(tab.Icon).Class("navigation-tabs__icon"));
                parts.Add(new Text(tab.LabelKey));
                if (tab.Count is { } count) parts.Add(new Span(new ValueText(count.ToString(System.Globalization.CultureInfo.CurrentCulture))).Class("navigation-tabs__count"));
                var link = new A([.. parts]).Attribute("href", tab.Href)
                    .Class(tab.Selected ? "navigation-tabs__link navigation-tabs__link--active"
                                        : "navigation-tabs__link");
                if (tab.Selected) link.Attribute("aria-current", "page");
                return (IUiComponent)link;
            }).ToArray();

            new Nav(links).Class(modifier is null ? "navigation-tabs" : $"navigation-tabs navigation-tabs--{modifier}")
                .Attribute("aria-label", texts[ariaLabelKey])
                .WriteTo(writer, encoder, texts);
        }
    }
}
