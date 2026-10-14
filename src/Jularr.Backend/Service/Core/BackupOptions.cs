namespace Jularr.Backend.Service.Core
{
    public sealed class BackupOptions
    {
        // Absolute folder for backups; unset disables them.
        public string? Path { get; set; }

        // Automatic backups; 0 turns them off.
        public int IntervalHours { get; set; } = 24;
        public int Keep { get; set; } = 7;

        // Protected credentials need the key ring of the instance.
        internal string? KeyDirectory { get; set; }
    }
}
