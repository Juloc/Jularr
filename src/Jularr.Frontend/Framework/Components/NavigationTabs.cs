using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record NavigationTab(string LabelKey, string Href, bool Selected);

    // A reusable component, not a page or its navigation policy.
    public sealed class NavigationTabs(
        IReadOnlyList<NavigationTab> tabs, string ariaLabelKey, string? modifier = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (tabs.Count == 0) return;

            var links = tabs.Select(tab =>
            {
                var link = new A(new Text(tab.LabelKey)).Attribute("href", tab.Href)
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
