using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Presentation labels, not locale mapping.
    public sealed class MediaLanguage(string flagIcon, string labelKey)
    {
        public string FlagIcon { get; } = flagIcon;
        public string LabelKey { get; } = labelKey;
    }

    public sealed class MediaLanguages(FrontendAssets assets, MediaLanguage? preferredDub, int dubCount, MediaLanguage? preferredSub, int subCount) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Div(Language("player-volume", "media.dubs", preferredDub, dubCount), Language("subtitles", "media.subs", preferredSub, subCount))
                .Class("media-card__languages").WriteTo(writer, encoder, texts);

            // Preference or distinct language count.
            IUiComponent Language(string icon, string key, MediaLanguage? preferred, int count)
            {
                IUiComponent value = preferred is null ? new ValueText(count.ToString(CultureInfo.CurrentCulture)) : new Flag(preferred.FlagIcon, assets);
                var label = preferred is null
                    ? string.Format(CultureInfo.CurrentCulture, texts["media.languageCount"], texts[key], count)
                    : string.Format(CultureInfo.CurrentCulture, texts["media.languagePreferred"], texts[key], texts[preferred.LabelKey]);
                return new IconInfo(new Span(new Icon(icon, assets), value).Class("media-card__language"), label);
            }
        }
    }
}
