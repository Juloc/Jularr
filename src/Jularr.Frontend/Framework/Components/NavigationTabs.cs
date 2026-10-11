using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// The page supplies only links the current user may actually browse.
public sealed record NavigationTab(string LabelKey, string Href, bool Selected);

public sealed class NavigationTabs(
    IReadOnlyList<NavigationTab> tabs, string ariaLabelKey, string cssClass) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        if (tabs.Count == 0) return;

        var links = tabs.Select(tab =>
        {
            var link = new A(new Text(tab.LabelKey)).Attribute("href", tab.Href);
            if (tab.Selected) link.Class("is-active").Attribute("aria-current", "page");
            return (IUiComponent)link;
        }).ToArray();

        new Nav(links).Class(cssClass)
            .Attribute("aria-label", texts[ariaLabelKey])
            .WriteTo(writer, encoder, texts);
    }
}
