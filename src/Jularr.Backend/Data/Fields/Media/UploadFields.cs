namespace Jularr.Backend.Data.Fields.Media
{
    public static class UploadFields
    {
        public static readonly FileField Volume = new() { MaximumBytes = 2_000_000_000, Extensions = [".cbz", ".cbr", ".cb7", ".epub", ".pdf"] };
    }
}
