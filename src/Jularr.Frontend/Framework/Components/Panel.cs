using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class Panel : IUiComponent
{
    public List<IUiComponent> Content { get; } = [];

    public Panel(Action<List<IUiComponent>> define) => define(Content);

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
        new Section([.. Content]).Class("ui-panel").WriteTo(writer, encoder, texts);
}
