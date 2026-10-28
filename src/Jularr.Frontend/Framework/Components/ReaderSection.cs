using System.Text.Encodings.Web;
using Jularr.Backend.Data.User.Reading.V1;

namespace Jularr.Frontend.Framework
{
    // A book section as prepared by the reading service; its HTML was sanitized there and is written as is.
    // A scrolled text begins with the way back to the previous chapter (to its end) and ends with the way on to the next one;
    // both only show while scrolling and are no blocks of the text.
    public sealed class ReaderSection(ReadingSectionV1 section, string? previous = null, string? next = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            writer.Write("<article class=\"reader__content\" data-role=\"content\">");
            if (previous is not null) Edge(writer, encoder, previous, "previous", texts["reader.chapter.previous"]);
            writer.Write(section.Html);
            if (next is not null) Edge(writer, encoder, next, "next", texts["reader.chapter.next"]);
            writer.Write("</article>");
        }

        private static void Edge(TextWriter writer, HtmlEncoder encoder, string href, string edge, string label)
        {
            writer.Write("<a class=\"button reader__chapter-edge reader__chapter-edge--");
            writer.Write(edge);
            writer.Write("\" data-chapter-edge=\"");
            writer.Write(edge);
            writer.Write("\" href=\"");
            writer.Write(encoder.Encode(href));
            writer.Write("\">");
            writer.Write(encoder.Encode(label));
            writer.Write("</a>");
        }
    }
}
