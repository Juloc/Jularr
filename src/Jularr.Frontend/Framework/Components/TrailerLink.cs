using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Opens a trailer on YouTube; the page itself frames nothing from there.
    public sealed class TrailerLink(string youTubeKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new A(new MediaImage("https://i.ytimg.com/vi/" + youTubeKey + "/hqdefault.jpg", "", 480, 360), new Span(new Text("details.watchTrailer")).Class("trailer-link__play"))
                .Class("trailer-link").Attribute("href", "https://www.youtube.com/watch?v=" + youTubeKey).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer")
                .WriteTo(writer, encoder, texts);
    }
}
