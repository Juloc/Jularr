using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Simple localized content list, not a settings-specific or fake navigation widget.
    public sealed class ItemList(params string[] resourceKeys) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var rows = resourceKeys.Select(key =>
                (IUiComponent)new Li(new Text(key)).Class("item-list__item")).ToArray();

            new Ul(rows).Class("item-list").WriteTo(writer, encoder, texts);
        }
    }
}
