namespace Jularr.Backend.Data.Enums.Playback
{
    // Where a marker comes from; the lower value wins when several sources know the same kind.
    public enum MediaMarkerSource : byte
    {
        Manual = 1,
        Sidecar = 2,
        Chapter = 3,
        Detected = 4
    }
}
