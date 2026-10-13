using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The way back to the saved reading position: identity, read share, one action and Dismiss; no reader controls.
    public sealed class ContinueReadingBar(FrontendAssets assets, string title, string? posterUrl, string? unit, decimal? progression, string href, string key) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var details = new List<IUiComponent> { new Strong(new ValueText(title)).Class("continue-bar__title") };
            if (unit is not null) details.Add(new Span(new ValueText(unit)).Class("continue-bar__meta"));
            var children = new List<IUiComponent>();
            if (progression is { } share)
            {
                var percent = MediaCard.ReadPercent(share);
                details.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["media.percentRead"], percent))).Class("continue-bar__meta"));
                children.Add(new Progress().Attribute("max", "100").Attribute("value", percent.ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", texts["media.readProgress"]).Class("continue-bar__progress"));
            }

            IUiComponent cover = posterUrl is null ? new Icon("reading", assets) : new MediaImage(posterUrl, "", 92, 138);
            // Cover and title continue as well; the action is the one keyboard stop.
            children.Add(new A(new Span(cover).Class("continue-bar__cover"), new Span([.. details]).Class("continue-bar__details"))
                .Class("continue-bar__identity").Attribute("href", href).Attribute("tabindex", "-1").Attribute("aria-hidden", "true"));
            children.Add(new A(new Icon("reading", assets), new Span(new Text("media.continueReading")).Class("continue-bar__label"))
                .Class("button button--primary continue-bar__action").Attribute("href", href).Attribute("aria-label", texts["media.continueReading"]));
            children.Add(new Button(new Icon("close", assets)).Class("button continue-bar__dismiss").Attribute("data-continue-dismiss", "true")
                .Attribute("aria-label", texts["continueReading.dismiss"]));
            new Section([.. children]).Class("continue-bar").Attribute("aria-label", texts["media.continueReading"]).Attribute("data-continue-key", key)
                .WriteTo(writer, encoder, texts);
        }
    }
}
