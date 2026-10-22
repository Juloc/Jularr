namespace Jularr.Backend.Data.Enums.Instance
{
    // How a service answered at its last check; Unknown is a service nothing has asked yet.
    public enum HealthStateType : byte
    {
        Available = 1,
        Unavailable = 2,
        Unknown = 3
    }
}
