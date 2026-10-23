namespace Jularr.Backend.Data.Enums.Media
{
    // What the viewer can do with a title: request it, wait for it or use it.
    public enum MediaStateType : byte
    {
        NotRequested = 1,
        Requested = 2,
        Available = 3
    }
}
