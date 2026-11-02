namespace Jularr.Backend.Data.Enums.Reading
{
    // How a book with a PDF is read: as the reflowed Smart Book, as the pages of the original, or as Smart Pages (a scan's pages in their own layout with
    // the text set anew). No choice means automatic.
    public enum ReadingView : byte
    {
        Smart = 1,
        Original = 2,
        SmartPages = 3
    }
}
