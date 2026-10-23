namespace Jularr.Backend.Data.Enums.Acquisition
{
    public enum AcquisitionRequestStatusType : byte
    {
        Open = 1,
        Approved = 2,
        Searching = 3,
        Downloading = 4,
        Importing = 5,
        Completed = 6,
        Rejected = 7,
        Failed = 8,
        Cancelled = 9
    }
}
