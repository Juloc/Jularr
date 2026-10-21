using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The label is a UI resource key, or a value of the data (a season, an edition) when IsValue is set; a count follows it in muted type.
    public sealed record NavigationTab(string LabelKey, string Href, bool Selected, bool IsValue = false, int? Count = null)
    {
        public IUiComponent Label() => IsValue ? new ValueText(LabelKey) : new Text(LabelKey);
    }

    // A reusable component, not a page or its navigation policy.
    public sealed class NavigationTabs(IReadOnlyList<NavigationTab> tabs, string ariaLabelKey, string? modifier = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (tabs.Count == 0) return;

            var links = tabs.Select(tab =>
            {
                var label = tab.Count is { } count
                    ? new IUiComponent[] { tab.Label(), new Span(new ValueText(count.ToString(CultureInfo.CurrentCulture))).Class("navigation-tabs__count") }
                    : [tab.Label()];
                var link = new A(label).Attribute("href", tab.Href)
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
