using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One entry of a sortable list: the key it is saved by, its title, the icon of its tile and whether it is switched on.
    public sealed record SortableItem(string Key, IUiComponent Title, string Icon, bool Shown, UiTone Tone = UiTone.Accent);

    // A list the person puts in order: rows with a drag handle, the position, an icon tile, the title with one short line (its state), a switch
    // and a menu of moves. The rows send their keys in the order they stand in (`orderName`) and the keys of the switched-on ones (`shownName`);
    // SortableList.entry.ts drags with the pointer (and moves with the arrow keys on the handle) and renumbers. Without script the form saves as it is shown.
    public sealed class SortableList(FrontendAssets assets, string orderName, string shownName, string labelKey, IReadOnlyList<SortableItem> items, string stateOnKey, string stateOffKey,
        bool disabled = false) : IUiComponent
    {
        private static long _next;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var list = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
            var rows = items.Select((item, index) =>
            {
                var id = "sortable-" + list + "-" + index.ToString(CultureInfo.InvariantCulture);
                var menu = id + "-menu";
                var moves = new[] { ("top", "sortable.top"), ("up", "sortable.up"), ("down", "sortable.down"), ("bottom", "sortable.bottom") }
                    .Select(move => (IUiComponent)new Button(new Text(move.Item2)).Class("sortable__move").Attribute("role", "menuitem").Attribute("data-sortable-move", move.Item1)).ToArray();
                var children = new List<IUiComponent>
                {
                    new Input().Attribute("type", "hidden").Attribute("name", orderName).Attribute("value", item.Key),
                    new Button(new Icon("grip", assets, 18)).Class("sortable__handle").Attribute("data-sortable-handle", "").Attribute("aria-describedby", id + "-title")
                        .TextAttribute("aria-label", "sortable.handle"),
                    new Span(new ValueText((index + 1).ToString(CultureInfo.InvariantCulture))).Class("sortable__number").Attribute("aria-hidden", "true"),
                    new IconTile(new Icon(item.Icon, assets), item.Tone),
                    new Span(new Span(item.Title).Class("sortable__title").Attribute("id", id + "-title"),
                        new Span(new Text(stateOnKey)).Class("sortable__line sortable__line--on"),
                        new Span(new Text(stateOffKey)).Class("sortable__line sortable__line--off")).Class("sortable__text"),
                    new Toggle(item.Title, item.Shown, disabled, labelHidden: true).Named(shownName, item.Key),
                    new Button(new Icon("dots", assets, 18)).Class("sortable__dots").Attribute("popovertarget", menu).Attribute("aria-haspopup", "menu").Attribute("data-sortable-menu", "")
                        .TextAttribute("aria-label", "sortable.menu"),
                    new Div(moves).Class("sortable__menu").Attribute("id", menu).Attribute("popover", "auto").Attribute("role", "menu").TextAttribute("aria-label", "sortable.menu")
                };
                return (IUiComponent)new Li([.. children]).Class("sortable__row").Attribute("data-sortable-row", "").Attribute("data-key", item.Key);
            }).ToArray();
            new Div(new Ol(rows).Class("sortable__rows").TextAttribute("aria-label", labelKey),
                new Span().Class("visually-hidden").Attribute("role", "status").Attribute("aria-live", "polite").Attribute("data-sortable-status", "")
                    .TextAttribute("data-format", "sortable.position"))
                .Class("sortable").Attribute("data-sortable", "").WriteTo(writer, encoder, texts);
        }
    }
}
