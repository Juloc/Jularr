using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public sealed record LayoutOption(string Icon, string LabelKey, string Href, bool Selected);

    // Icon links that choose how one list is shown (rows or cards); the address already holds the choice, so it works without script.
    public sealed class LayoutSwitch(FrontendAssets assets, IReadOnlyList<LayoutOption> options, string ariaLabelKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var links = options.Select(option =>
            {
                var link = new A(new Icon(option.Icon, assets, 16)).Attribute("href", option.Href).TextAttribute("aria-label", option.LabelKey).TextAttribute("title", option.LabelKey)
                    .Class(option.Selected ? "layout-switch__link layout-switch__link--selected" : "layout-switch__link");
                return (IUiComponent)(option.Selected ? link.Attribute("aria-current", "true") : link);
            });

            new Div([.. links]).Class("layout-switch").Attribute("role", "group").Attribute("aria-label", texts[ariaLabelKey]).WriteTo(writer, encoder, texts);
        }
    }
}
