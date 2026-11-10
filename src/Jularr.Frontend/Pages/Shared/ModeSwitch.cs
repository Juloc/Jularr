using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Shared
{
    // The mode switch under the brand of the user sidebar: the active mode with an arrow opens the list of the five modes (a native details, closed by an outside click or Escape,
    // SelectLinkMenu.entry.ts). Every entry is a link (`/?mode=`) that also keeps the mode in its cookie, so it works without script; the list stays in the sidebar's flow,
    // where nothing can cut it off, and a tablet shows the icons only.
    public sealed class ModeSwitch(FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var active = texts.Mode;
            var button = new Summary(new Span(new Icon(active.Icon, assets, 18)).Class("mode-switch__icon"), new Span(new Text(active.LabelKey)).Class("mode-switch__label"),
                new Span(new Icon("chevron-down", assets, 14)).Class("mode-switch__chevron")).Class("mode-switch__button").Attribute("aria-label", texts["mode.switch"]);
            var items = UserModes.All.Select(mode =>
            {
                var link = new A(new Span(new Icon(mode.Icon, assets, 18)).Class("mode-switch__icon"), new Span(new Text(mode.LabelKey)).Class("mode-switch__label"))
                    .Class(mode == active ? "mode-switch__item is-active" : "mode-switch__item").Attribute("href", "/?" + UserModes.Parameter + "=" + mode.Key);
                return (IUiComponent)(mode == active ? link.Attribute("aria-current", "true") : link);
            });
            new Details(button, new Nav([.. items]).Class("mode-switch__list").Attribute("aria-label", texts["mode.switch"])).Class("mode-switch").WriteTo(writer, encoder, texts);
        }
    }
}
