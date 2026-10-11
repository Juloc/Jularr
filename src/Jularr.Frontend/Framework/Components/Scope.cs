using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// Scope labels are not clickable until a typed filter/read contract exists.
public sealed class UiScope(IReadOnlyList<string> keys) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Ul(keys.Select(key => (IUiComponent)new Li(new Text(key))).ToArray())
            .Class("page-scope").WriteTo(writer, encoder, texts);
    }
}
