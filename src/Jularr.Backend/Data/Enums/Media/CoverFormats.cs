namespace Jularr.Backend.Data.Enums.Media
{
    // The image formats beyond JPEG that a browser names in its Accept header; JPEG answers for everything else.
    [Flags]
    public enum CoverFormats : byte
    {
        None = 0,
        WebP = 1,
        Jxl = 2
    }
}
