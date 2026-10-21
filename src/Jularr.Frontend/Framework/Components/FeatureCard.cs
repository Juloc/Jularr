namespace Jularr.Frontend.Framework
{
    // The mockups' card grid: as many cards per row as fit. Pages put FeatureCards in it.
    public sealed class CardGrid : HtmlElement
    {
        public CardGrid(params IUiComponent[] cards) : base("div", cards)
        {
            Class("card-grid");
        }
    }

    // One card of a CardGrid (a connection, a provider): an icon tile, a name with one line under it and its state chip in the head; body rows under them;
    // actions at the foot, the first group at the start, the second at the end. Name and line are one line each and show whole in a tip when clipped.
    // With an id the card is a Region: an action inside it swaps just this card.
    public sealed class FeatureCard : HtmlElement
    {
        public FeatureCard(IconTile icon, string name, string? line, IUiComponent? state, IReadOnlyList<IUiComponent> body, IReadOnlyList<IUiComponent> start, IReadOnlyList<IUiComponent>? end = null, string? id = null)
            : base("article", Parts(icon, name, line, state, body, start, end))
        {
            Class("feature-card");
            if (id is null) return;
            Attribute("id", id);
            Attribute("data-region", "");
        }

        private static IUiComponent[] Parts(IconTile icon, string name, string? line, IUiComponent? state, IReadOnlyList<IUiComponent> body, IReadOnlyList<IUiComponent> start, IReadOnlyList<IUiComponent>? end)
        {
            var title = new List<IUiComponent> { new Span(new Truncated(name)).Class("feature-card__name") };
            if (line is not null) title.Add(new Span(new Truncated(line)).Class("feature-card__line"));
            var head = new List<IUiComponent> { icon, new Div([.. title]).Class("feature-card__title") };
            if (state is not null) head.Add(state);

            var parts = new List<IUiComponent> { new Div([.. head]).Class("feature-card__head") };
            parts.AddRange(body);
            if (start.Count > 0 || end is { Count: > 0 })
            {
                parts.Add(new Div(new Div([.. start]).Class("feature-card__group"), new Div([.. end ?? []]).Class("feature-card__group")).Class("feature-card__foot"));
            }

            return [.. parts];
        }
    }
}
