namespace Jularr.Backend.Data.Enums.Instance
{
    // How a service answered at its last check; Unknown is a service nothing has asked yet.
    public enum HealthStateType : byte
    {
        Available = 1,
        Unavailable = 2,
        Unknown = 3,
        // An indexer resting after it limited or refused requests; it is not asked until its pause ends.
        Paused = 4
    }
}
