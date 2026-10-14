namespace Jularr.Backend.Data.Fields.Media
{
    public static class UploadFields
    {
        // The whole request: the volume and its form values.
        public const long VolumeRequestBytes = 2_001_048_576;

        public static readonly FileField Volume = new() { MaximumBytes = VolumeRequestBytes - 1_048_576, Extensions = [".cbz", ".cbr", ".cb7", ".epub", ".pdf"] };
    }
}
