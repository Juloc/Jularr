namespace Jularr.Backend.Data.Enums.Reading
{
    // What a re-translation in the Reader covers: one paragraph (block), the section it is in, or the whole book.
    public enum ReadingTranslationScope : byte
    {
        Block = 1,
        Section = 2,
        Book = 3
    }
}
