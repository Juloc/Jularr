using System.Text.Encodings.Web;
using Jularr.Backend.Data.User.Reading.V1;

namespace Jularr.Frontend.Framework
{
    // A book section as prepared by the reading service; its HTML was sanitized there and is written as is.
    public sealed class ReaderSection(ReadingSectionV1 section) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            writer.Write("<article class=\"reader__content\" data-role=\"content\">");
            writer.Write(section.Html);
            writer.Write("</article>");
        }
    }
}
