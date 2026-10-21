using System.Globalization;
using System.Net;
using System.Text;
using Jularr.Backend.Data.Logic.Reading;

namespace Jularr.Backend.Logic.Reading
{
    // A converted PDF as reader sections: one per chapter, its paragraphs, pictures and figures prepared like any book text.
    internal static class PdfBookHtml
    {
        public static string SectionPath(int index) => ":section/" + index.ToString("D5", CultureInfo.InvariantCulture);

        public static PdfPreparedBook Prepare(PdfBookContent book, long documentId)
        {
            var sections = new List<PdfPreparedSection>();
            var toc = new List<ReadingStructureEntry>();
            var noSections = new Dictionary<string, int>(StringComparer.Ordinal);
            var pictures = book.Images.ToDictionary(image => image.Path, image => image.MediaType, StringComparer.Ordinal);
            for (var index = 0; index < book.Chapters.Count; index++)
            {
                var chapter = book.Chapters[index];
                var html = new StringBuilder("<html><body>");
                if (chapter.Title is { } heading)
                {
                    html.Append("<h2>").Append(WebUtility.HtmlEncode(heading)).Append("</h2>");
                    toc.Add(new ReadingStructureEntry(heading, index, 1, null));
                }

                foreach (var block in chapter.Blocks)
                {
                    html.Append(Markup(block));
                }

                var path = "pdf/" + index.ToString("D5", CultureInfo.InvariantCulture) + ".xhtml";
                var prepared = ReadingHtml.Prepare(html.Append("</body></html>").ToString(), path, documentId, noSections, pictures);
                sections.Add(new PdfPreparedSection(new ReadingStructureSection(path, chapter.Title, prepared.Characters, prepared.Blocks), prepared.Html));
            }

            return new PdfPreparedBook(sections, toc);
        }

        // A paragraph, a small picture in the line, a figure with its alternative text and caption, or the stand-in for a page only the original shows.
        private static string Markup(PdfBlock block)
        {
            if (block.Figure == ReadingHtml.OriginalFigure)
            {
                return "<figure data-figure=\"original\" data-page=\"" + block.Page.ToString(CultureInfo.InvariantCulture) + "\"></figure>";
            }

            if (block.Image is not { } picture)
            {
                return "<p>" + WebUtility.HtmlEncode(block.Text) + "</p>";
            }

            var source = WebUtility.HtmlEncode(picture["pdf/".Length..]);
            return block.Figure is null
                ? "<p><img alt=\"\" src=\"" + source + "\" /></p>"
                : "<figure data-figure=\"" + block.Figure + "\"><img alt=\"" + WebUtility.HtmlEncode(block.Alt ?? "") + "\" src=\"" + source + "\" />"
                    + (block.Caption is { } caption ? "<figcaption>" + WebUtility.HtmlEncode(caption) + "</figcaption>" : "") + "</figure>";
        }
    }

    internal sealed record PdfPreparedSection(ReadingStructureSection Structure, string Html);

    internal sealed record PdfPreparedBook(IReadOnlyList<PdfPreparedSection> Sections, IReadOnlyList<ReadingStructureEntry> Toc);
}
