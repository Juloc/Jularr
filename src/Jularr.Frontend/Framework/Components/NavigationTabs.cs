using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The label is a UI resource key, or a value of the data (a season, an edition) when IsValue is set; a count follows it in muted type, an icon leads it (the state a filter chip stands for).
    // A divider is the thin line that sets the chips after it apart from the ones before (the page's own filters after its sibling pages).
    public sealed record NavigationTab(string LabelKey, string Href, bool Selected, bool IsValue = false, int? Count = null, IUiComponent? Icon = null, bool Divider = false)
    {
        public IUiComponent Label() => IsValue ? new ValueText(LabelKey) : new Text(LabelKey);
    }

    // A reusable component, not a page or its navigation policy.
    public sealed class NavigationTabs(IReadOnlyList<NavigationTab> tabs, string ariaLabelKey, string? modifier = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (tabs.Count == 0) return;

            var links = tabs.SelectMany(tab =>
            {
                var parts = new List<IUiComponent>();
                if (tab.Icon is not null) parts.Add(new Span(tab.Icon).Class("navigation-tabs__icon"));
                parts.Add(tab.Label());
                if (tab.Count is { } count) parts.Add(new Span(new ValueText(count.ToString(System.Globalization.CultureInfo.CurrentCulture))).Class("navigation-tabs__count"));
                var link = new A([.. parts]).Attribute("href", tab.Href)
                    .Class(tab.Selected ? "navigation-tabs__link navigation-tabs__link--active"
                                        : "navigation-tabs__link");
                if (tab.Selected) link.Attribute("aria-current", "page");
                return tab.Divider ? new IUiComponent[] { new Span().Class("navigation-tabs__divider").Attribute("aria-hidden", "true"), link } : [link];
            }).ToArray();

            new Nav(links).Class(modifier is null ? "navigation-tabs" : $"navigation-tabs navigation-tabs--{modifier}")
                .Attribute("aria-label", texts[ariaLabelKey])
                .WriteTo(writer, encoder, texts);
        }
    }
}
