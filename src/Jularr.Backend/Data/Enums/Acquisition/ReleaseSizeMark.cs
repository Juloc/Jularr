namespace Jularr.Backend.Data.Enums.Acquisition
{
    // A release's size per volume against the limits of its kind.
    public enum ReleaseSizeMark : byte
    {
        Fits = 1,
        BelowMinimum = 2,
        AboveMaximum = 3,
        // The size or the number of volumes is unknown, so the size per volume cannot be judged.
        Unknown = 4,
        // The kind has no size limits.
        NoLimit = 5
    }
}
