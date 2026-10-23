using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One highlight. Group and Pass order the choice: Pass is the title's rank within its group; a continued title ignores both.
    // A slide without a backdrop shows its poster over a blurred copy of itself.
    public sealed record HeroSlide(string Title, string? BackdropUrl, string? PosterUrl, string? Overline, string? Overview, string? ActionKey, string? ActionHref, string? ActionIcon,
        string? DetailsHref, string Group, int Pass, bool IsContinue = false);

    // The page decides which titles are highlighted; this shows one at a time with arrows, a position bar and a pause.
    // The script chooses from the slides by the device's preferences (see HeroCarousel.ts).
    public sealed class HeroCarousel(FrontendAssets assets, IReadOnlyList<HeroSlide> slides, string preferencesKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (slides.Count == 0) return;

            var parts = new List<IUiComponent>();
            for (var index = 0; index < slides.Count; index++)
            {
                var slide = slides[index];
                var art = new List<IUiComponent>();
                if (slide.BackdropUrl is not null)
                {
                    art.Add(new Div(new MediaImage(slide.BackdropUrl, "", 1280, 720, priority: index == 0)).Class("hero-carousel__backdrop"));
                }
                else if (slide.PosterUrl is not null)
                {
                    art.Add(new Div(new MediaImage(slide.PosterUrl, "", 500, 750, priority: index == 0)).Class("hero-carousel__backdrop hero-carousel__backdrop--blur"));
                    art.Add(new Div(new MediaImage(slide.PosterUrl, "", 500, 750, priority: index == 0)).Class("hero-carousel__poster"));
                }

                var body = new List<IUiComponent>();
                if (slide.Overline is not null) body.Add(new P(new ValueText(slide.Overline)).Class("hero-carousel__overline"));
                body.Add(new H2(new ValueText(slide.Title)).Class("hero-carousel__title"));
                // Clamped to two lines: the whole text is in the page, in the tooltip and behind Details.
                if (slide.Overview is not null) body.Add(new P(new ValueText(slide.Overview)).Class("hero-carousel__overview").Attribute("title", slide.Overview));

                var actions = new List<IUiComponent>();
                if (slide.ActionKey is not null && slide.ActionHref is not null)
                {
                    actions.Add(new A([.. slide.ActionIcon is null ? [] : new IUiComponent[] { new Icon(slide.ActionIcon, assets, 18) }, new Text(slide.ActionKey)]).Class("button button--primary").Attribute("href", slide.ActionHref));
                }

                if (slide.DetailsHref is not null)
                {
                    actions.Add(new A(new Icon("info", assets, 18), new Text("media.details")).Class("button hero-carousel__details").Attribute("href", slide.DetailsHref));
                }

                if (actions.Count > 0) body.Add(new Div([.. actions]).Class("hero-carousel__actions"));

                var element = new Div(new Div([.. art]).Class("hero-carousel__art"), new Div([.. body]).Class("hero-carousel__body"))
                    .Class(slide.BackdropUrl is null && slide.PosterUrl is null ? "hero-carousel__slide hero-carousel__slide--plain" : "hero-carousel__slide")
                    .Attribute("data-hero-slide", "").Attribute("data-hero-group", slide.Group).Attribute("data-hero-pass", slide.Pass.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (slide.IsContinue) element.Attribute("data-hero-continue", "");
                if (index > 0) element.Attribute("hidden", "hidden");
                parts.Add(element);
            }

            // The script shows these once it has chosen more than one slide.
            parts.Add(new Button(new Icon("chevron-left", assets)).IconOnly(texts["hero.previous"]).Class("button button--icon hero-carousel__arrow hero-carousel__arrow--previous")
                .Attribute("data-hero-step", "-1").Attribute("hidden", "hidden"));
            parts.Add(new Button(new Icon("chevron-left", assets)).IconOnly(texts["hero.next"]).Class("button button--icon hero-carousel__arrow hero-carousel__arrow--next")
                .Attribute("data-hero-step", "1").Attribute("hidden", "hidden"));
            parts.Add(new Div(new Div().Class("hero-carousel__segments"),
                new Button(new Span(new Icon("pause", assets, 15)).Class("hero-carousel__pause-icon hero-carousel__pause-icon--pause"),
                        new Span(new Icon("play", assets, 15)).Class("hero-carousel__pause-icon hero-carousel__pause-icon--play"))
                    .IconOnly(texts["hero.pause"]).Class("button button--icon hero-carousel__pause").Attribute("data-hero-pause", "").Attribute("aria-pressed", "false"),
                new Span().Class("hero-carousel__counter")).Class("hero-carousel__controls").Attribute("hidden", "hidden"));

            new Section([.. parts]).Class("hero-carousel").Attribute("tabindex", "0").Attribute("aria-roledescription", "carousel").Attribute("aria-label", texts["hero.label"])
                .Attribute("data-hero-carousel", "").Attribute("data-hero-preferences", preferencesKey)
                .Attribute("data-hero-pause-label", texts["hero.pause"]).Attribute("data-hero-resume-label", texts["hero.resume"])
                .Attribute("data-hero-show-label", texts["hero.show"])
                .WriteTo(writer, encoder, texts);
        }
    }
}
