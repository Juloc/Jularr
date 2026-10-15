namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Lists of the Wanted page: every target, the ones still missing media, the ones below their profile's cutoff.
    public enum WantedViewType : byte
    {
        All = 0,
        Missing = 1,
        CutoffUnmet = 2
    }
}
