namespace Jularr.Backend.Data.Enums.Reading
{
    // How a book with a PDF is read: as the reflowed Smart Book, or as the pages of the original. No choice means automatic.
    public enum ReadingView : byte
    {
        Smart = 1,
        Original = 2
    }
}
