namespace Jularr.Backend.Data.Enums.Library
{
    // A file's size against the profile's range for its quality: in range, small for its quality and codec, or larger than the range allows.
    public enum FileSizeGrade : byte
    {
        Unknown = 0,
        Good = 1,
        Small = 2,
        Large = 3
    }
}
