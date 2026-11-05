namespace Jularr.Backend.Data.Enums.Operation
{
    // Why a backup was made; automatic and update backups share the keep limit, manual ones stay until a person deletes them.
    public enum BackupKindType : byte
    {
        Automatic = 1,
        Update = 2,
        Manual = 3
    }
}
