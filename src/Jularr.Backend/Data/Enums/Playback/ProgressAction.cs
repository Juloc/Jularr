namespace Jularr.Backend.Data.Enums.Playback
{
    // A profile's own change of its progress; starting over is Unwatched followed by playing from the beginning.
    public enum ProgressAction : byte
    {
        Watched = 1,
        Unwatched = 2
    }
}
