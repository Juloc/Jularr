using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record UiNavItem(string Key, string Href);

    public sealed class UiSubnav(IReadOnlyList<UiNavItem> items) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new NavigationTabs(items.Select(item => new NavigationTab(item.Key, item.Href, false)).ToArray(), "nav.primary")
                .WriteTo(writer, encoder, texts);
        }
    }
}
