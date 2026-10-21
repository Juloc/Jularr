using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A button that opens a small panel of actions or of one short form (the more menu, a request); native details, closed by an outside click or Escape
    // (SelectLinkMenu.entry.ts), so it works without script. The button takes the look of the shared buttons through its classes; an item of the panel is a
    // link or a button of its own form with the action-menu__item class.
    public sealed class ActionMenu(IUiComponent summary, IReadOnlyList<IUiComponent> content, string buttonClass, string? label = null, bool open = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var button = new Summary(summary).Class("action-menu__button " + buttonClass);
            if (label is not null) button.Attribute("aria-label", texts[label]);
            var menu = new Details(button, new Div([.. content]).Class("action-menu__panel")).Class("action-menu");
            if (open) menu.Attribute("open", "open");
            menu.WriteTo(writer, encoder, texts);
        }
    }
}
