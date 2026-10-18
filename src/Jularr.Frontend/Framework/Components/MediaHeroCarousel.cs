using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Every slide already belongs to the authorized profile and was selected on the
    // server. This component never fetches providers or modifies library state.
    public sealed class MediaHeroCarousel(FrontendAssets assets, IReadOnlyList<MediaHero> slides) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (slides.Count == 0) return;
            if (slides.Count > HeroSelection.MaximumSlides) throw new ArgumentOutOfRangeException(nameof(slides));

            var items = slides.Select((slide, index) =>
            {
                // Only the first image is above the fold; avoid eight eager downloads.
                slide.Priority = index == 0;
                var wrapper = new Div(slide).Class(index == 0
                    ? "media-hero-carousel__slide is-active" : "media-hero-carousel__slide")
                    .Attribute("data-hero-slide-item", index.ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-hidden", index == 0 ? "false" : "true");
                if (index != 0) wrapper.Attribute("inert", "");
                return (IUiComponent)wrapper;
            }).ToArray();

            var parts = new List<IUiComponent>
            {
                new Div([.. items]).Class("media-hero-carousel__slides")
            };

            if (slides.Count > 1)
            {
                parts.Add(new Div(
                    new Button(new Icon("chevron-left", assets))
                        .Class("media-hero-carousel__arrow button")
                        .Attribute("type", "button")
                        .Attribute("data-hero-direction", "previous")
                        .Attribute("aria-label", texts["media.shelfPrevious"]),
                    new Button(new Icon("chevron-right", assets))
                        .Class("media-hero-carousel__arrow button")
                        .Attribute("type", "button")
                        .Attribute("data-hero-direction", "next")
                        .Attribute("aria-label", texts["media.shelfNext"])
                ).Class("media-hero-carousel__arrows"));

                parts.Add(new Div([.. slides.Select((_, index) => (IUiComponent)new Button(new Span())
                    .Class("media-hero-carousel__segment")
                    .Attribute("type", "button")
                    .Attribute("data-hero-slide", index.ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-label", (index + 1).ToString(CultureInfo.InvariantCulture))
                    .Attribute("aria-current", index == 0 ? "true" : "false"))])
                    .Class("media-hero-carousel__segments"));
            }

            new Section([.. parts])
                .Class("media-hero-carousel")
                .Attribute("data-hero-carousel", "true")
                .Attribute("aria-roledescription", "carousel")
                .WriteTo(writer, encoder, texts);
        }
    }
}
