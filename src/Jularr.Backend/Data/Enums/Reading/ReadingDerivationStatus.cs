namespace Jularr.Backend.Data.Enums.Reading
{
    // The reading copy of a PDF: being prepared, written, not made because the PDF stays as it is, or failed.
    public enum ReadingDerivationStatus : byte
    {
        Preparing = 1,
        Ready = 2,
        Declined = 3,
        Failed = 4
    }
}
