namespace Jularr.Frontend.Framework
{
    // Decorative scene only. ReaderFrame retains the canonical, accessible content.
    public sealed class ReaderBook() : HtmlElement("div",
        new Div(new Div(new Div().Class("reader-book__solid").Attribute("data-book-solid", ""), Half("left"), Half("right"))
                .Class("reader-book__body"),
            new Div(new Div().Class("reader-book__gutter"),
                new Template(new Div(new Div(
                    new Div().Class("reader-book__fold-front").Attribute("data-book-front", ""),
                    new Div(new Div().Class("reader-book__back-print").Attribute("data-book-back-print", "")).Class("reader-book__fold-back").Attribute("data-book-back", ""),
                    new Div().Class("reader-book__curl").Attribute("data-book-curl", ""),
                    new Div().Class("reader-book__fold-shadow").Attribute("data-book-fold-shadow", ""),
                    new Div().Class("reader-book__fold-light").Attribute("data-book-fold-light", ""))
                    .Class("reader-book__fold-surface")).Class("reader-book__turn").Attribute("data-book-turn", ""))
                    .Attribute("data-book-leaf-template", ""))
                .Class("reader-book__body reader-book__body--overlay").Attribute("data-book-leaves", ""))
            .Class("reader-book__camera"))
    {
        private static IUiComponent Half(string side) => new Div(
            new Div(new Div().Class("reader-book__print").Attribute("data-book-page", side))
                .Class("reader-book__sheet").Attribute("data-book-sheet", side))
            .Class("reader-book__half reader-book__half--" + side);

    }
}
