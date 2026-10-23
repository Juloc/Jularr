namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Which targets the automatic search takes first; it changes the order only, never a waiting time.
    public enum WantedPriorityType : byte
    {
        Normal = 1,
        High = 2
    }
}
