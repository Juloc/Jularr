using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiPlayer : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        new Div(
            new Video().Class("player-video")
                .Attribute("controls", "controls")
                .Attribute("playsinline", "playsinline")
                .Attribute("preload", "none")
                .Attribute("aria-label", texts["player.title"])
        ).Class("player-surface").WriteTo(writer, encoder, texts);
    }
}
