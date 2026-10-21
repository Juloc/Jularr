using System.Text;

namespace Jularr.Backend.Tests
{
    // PDFs written by hand for what the library cannot make: scans whose page picture is JPEG 2000, which PdfPig does not decode and browsers rarely show, under an invisible text layer.
    internal static class HandPdf
    {
        // Each page: one such picture over the whole page and the given lines of invisible text; a page without lines is a plate or a blank, by the size of its picture.
        public static byte[] ScannedPages(IReadOnlyList<string[]> pages, int pictureBytes)
        {
            var objects = new List<byte[]>();
            byte[] Text(string text) => Encoding.Latin1.GetBytes(text);

            // 1 catalog, 2 pages, 3 font; each page has its page object, content and picture.
            var kids = string.Join(' ', Enumerable.Range(0, pages.Count).Select(index => 4 + index * 3 + " 0 R"));
            objects.Add(Text("<< /Type /Catalog /Pages 2 0 R >>"));
            objects.Add(Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>"));
            objects.Add(Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
            for (var index = 0; index < pages.Count; index++)
            {
                var first = 4 + index * 3;
                objects.Add(Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> /XObject << /Im0 {first + 2} 0 R >> >> /Contents {first + 1} 0 R >>"));
                var content = new StringBuilder("q 595 0 0 842 0 0 cm /Im0 Do Q BT /F1 9 Tf 3 Tr\n");
                for (var line = 0; line < pages[index].Length; line++)
                {
                    content.Append($"1 0 0 1 54 {780 - line * 14} Tm ({pages[index][line].Replace("(", "\\(").Replace(")", "\\)")}) Tj\n");
                }

                content.Append("ET");
                objects.Add(Stream("<< /Length {0} >>", Text(content.ToString())));
                var picture = new byte[pictureBytes];
                new Random(index + 1).NextBytes(picture);
                new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }.CopyTo(picture, 0);
                objects.Add(Stream("<< /Type /XObject /Subtype /Image /Width 100 /Height 140 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {0} >>", picture));
            }

            using var output = new MemoryStream();
            output.Write(Text("%PDF-1.5\n"));
            var offsets = new List<long>();
            for (var index = 0; index < objects.Count; index++)
            {
                offsets.Add(output.Position);
                output.Write(Text($"{index + 1} 0 obj\n"));
                output.Write(objects[index]);
                output.Write(Text("\nendobj\n"));
            }

            var table = output.Position;
            output.Write(Text($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
            foreach (var offset in offsets)
            {
                output.Write(Text(offset.ToString("D10") + " 00000 n \n"));
            }

            output.Write(Text($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n"));
            return output.ToArray();
        }

        private static byte[] Stream(string dictionary, byte[] data)
        {
            using var stream = new MemoryStream();
            stream.Write(Encoding.Latin1.GetBytes(dictionary.Replace("{0}", data.Length.ToString()) + "\nstream\n"));
            stream.Write(data);
            stream.Write(Encoding.Latin1.GetBytes("\nendstream"));
            return stream.ToArray();
        }
    }
}
