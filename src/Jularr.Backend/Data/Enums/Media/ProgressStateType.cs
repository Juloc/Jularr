namespace Jularr.Backend.Data.Enums.Media
{
    // Where a profile stands with a title, derived from its progress; not a stored catalog.
    public enum ProgressStateType : byte
    {
        NotStarted = 1,
        InProgress = 2,
        Finished = 3
    }
}
