using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiDetailHero : IUiComponent
{
    // Do not fabricate title, art or year before the Work details service returns.
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
        new Header().Class("detail-hero").WriteTo(writer, encoder, texts);
}
