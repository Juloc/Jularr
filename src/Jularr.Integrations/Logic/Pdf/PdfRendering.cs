using System.Runtime.InteropServices;
using PDFiumCore;
using SkiaSharp;

namespace Jularr.Integrations.Logic.Pdf
{
    // A PDF opened by PDFium: its pages drawn as a reader sees them, scanned pictures in JPEG 2000 or JBIG2 included, which PdfPig cannot decode, and the
    // characters of its text layer with their boxes. PDFium is not thread-safe, so every call holds one lock; the bytes stay pinned while the document is open.
    public sealed class PdfRendering : IDisposable
    {
        private const int RenderAnnotations = 0x01;

        // With this flag (FPDF_REVERSE_BYTE_ORDER) the bytes PDFium writes match Skia's blue-first colour type; without it red and blue trade places.
        private const int ByteOrder = 0x10;
        private const long MaximumPixels = 60_000_000;

        private static readonly Lock Gate = new();
        private static bool _isInitialized;

        private readonly GCHandle _bytes;
        private readonly FpdfDocumentT _document;

        private PdfRendering(GCHandle bytes, FpdfDocumentT document, int pages)
        {
            _bytes = bytes;
            _document = document;
            Pages = pages;
        }

        public int Pages { get; }

        public static PdfRendering Open(byte[] pdf)
        {
            lock (Gate)
            {
                if (!_isInitialized)
                {
                    fpdfview.FPDF_InitLibrary();
                    _isInitialized = true;
                }

                var bytes = GCHandle.Alloc(pdf, GCHandleType.Pinned);
                var document = fpdfview.FPDF_LoadMemDocument(bytes.AddrOfPinnedObject(), pdf.Length, null);
                if (document is null || document.__Instance == IntPtr.Zero)
                {
                    bytes.Free();
                    throw new InvalidDataException("PDFium cannot open the PDF.");
                }

                return new PdfRendering(bytes, document, fpdfview.FPDF_GetPageCount(document));
            }
        }

        // The page, counted from 1, drawn on white as it shows (its /Rotate applied), its longer side this many pixels.
        public SKBitmap Render(int number, int side)
        {
            lock (Gate)
            {
                var page = Load(number);
                try
                {
                    var (width, tall) = (fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
                    var scale = side / Math.Max(width, tall);
                    var pixels = (Width: Math.Max(1, (int)Math.Round(width * scale)), Height: Math.Max(1, (int)Math.Round(tall * scale)));
                    if ((long)pixels.Width * pixels.Height > MaximumPixels)
                    {
                        throw new InvalidDataException("The page is too large to draw.");
                    }

                    // PDFium writes red first when asked to reverse its byte order: the bitmap says so, or red and blue trade places.
                    var bitmap = new SKBitmap(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var target = fpdfview.FPDFBitmapCreateEx(pixels.Width, pixels.Height, 4, bitmap.GetPixels(), bitmap.RowBytes);
                    try
                    {
                        fpdfview.FPDFBitmapFillRect(target, 0, 0, pixels.Width, pixels.Height, 0xFFFFFFFF);
                        fpdfview.FPDF_RenderPageBitmap(target, page, 0, 0, pixels.Width, pixels.Height, 0, RenderAnnotations | ByteOrder);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(target);
                    }

                    return bitmap;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        // The words of the page's text layer, a scan's hidden recognition layer included, with their boxes in fractions of the page from its top left.
        public IReadOnlyList<PdfLayerWord> Words(int number)
        {
            lock (Gate)
            {
                var page = Load(number);
                var text = fpdf_text.FPDFTextLoadPage(page);
                try
                {
                    var (width, height) = (fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
                    var words = new List<PdfLayerWord>();
                    var current = new System.Text.StringBuilder();
                    double left = 0, top = 0, right = 0, bottom = 0;
                    void End()
                    {
                        if (current.Length > 0)
                        {
                            words.Add(new PdfLayerWord(current.ToString(), left / width, 1 - top / height, right / width, 1 - bottom / height));
                            current.Clear();
                        }
                    }

                    for (var index = 0; index < fpdf_text.FPDFTextCountChars(text); index++)
                    {
                        // PDFium marks a hyphen it generated at a line end with 2.
                        var character = (char)fpdf_text.FPDFTextGetUnicode(text, index);
                        double l = 0, r = 0, b = 0, t = 0;
                        fpdf_text.FPDFTextGetCharBox(text, index, ref l, ref r, ref b, ref t);
                        if (char.IsWhiteSpace(character) || character == (char)2 || r <= l)
                        {
                            End();
                            continue;
                        }

                        (left, top, right, bottom) = current.Length == 0 ? (l, t, r, b) : (Math.Min(left, l), Math.Max(top, t), Math.Max(right, r), Math.Min(bottom, b));
                        current.Append(character);
                    }

                    End();
                    return words;
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(text);
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        private FpdfPageT Load(int number)
        {
            var page = number >= 1 && number <= Pages ? fpdfview.FPDF_LoadPage(_document, number - 1) : null;
            return page is null || page.__Instance == IntPtr.Zero ? throw new InvalidDataException("PDFium cannot open the page.") : page;
        }

        public void Dispose()
        {
            lock (Gate)
            {
                fpdfview.FPDF_CloseDocument(_document);
                _bytes.Free();
            }
        }
    }

    // A word of a PDF's text layer: its text and its box in fractions of the page, measured from the top left.
    public sealed record PdfLayerWord(string Text, double Left, double Top, double Right, double Bottom);
}
