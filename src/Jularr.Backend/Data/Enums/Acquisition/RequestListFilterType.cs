namespace Jularr.Backend.Data.Enums.Acquisition
{
    // The views of a person's own requests: waiting for approval, being fetched, available, failed, and everything finished.
    public enum RequestListFilterType : byte
    {
        All = 0,
        Awaiting = 1,
        Active = 2,
        Available = 3,
        Problems = 4,
        History = 5
    }
}
