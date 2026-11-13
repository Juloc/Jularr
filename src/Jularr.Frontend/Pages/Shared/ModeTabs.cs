using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // The five modes as tabs in the header of the user area, beside the search: each a link (`/?mode=`) that also keeps the mode in its cookie, so it works without script.
    // A phone shows the icons only, the active one tinted; its name stays the link's name for assistive technology.
    public sealed class ModeTabs(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var active = texts.Mode;
            var tabs = UserModes.All.Select(mode =>
            {
                var link = new A(new Span(new Icon(mode.Icon, assets, 16)).Class("mode-tabs__icon"), new Span(new Text(mode.LabelKey)).Class("mode-tabs__label"))
                    .Class(mode == active ? "mode-tabs__tab is-active" : "mode-tabs__tab").Attribute("href", "/?" + UserModes.Parameter + "=" + mode.Key)
                    .TextAttribute("title", mode.LabelKey);
                return (IUiComponent)(mode == active ? link.Attribute("aria-current", "true") : link);
            });
            new Nav([.. tabs]).Class("mode-tabs").Attribute("aria-label", texts["mode.switch"]).WriteTo(writer, encoder, texts);
        }
    }
}
