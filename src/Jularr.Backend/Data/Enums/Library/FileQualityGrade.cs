namespace Jularr.Backend.Data.Enums.Library
{
    // How a file's quality stands against its profile: at or above the cutoff, below it (upgrade wanted) or not allowed at all.
    public enum FileQualityGrade : byte
    {
        Unknown = 0,
        Met = 1,
        Upgrade = 2,
        NotAllowed = 3
    }
}
