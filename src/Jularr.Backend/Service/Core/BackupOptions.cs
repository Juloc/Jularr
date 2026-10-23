namespace Jularr.Backend.Service.Core
{
    public sealed class BackupOptions
    {
        // Absolute folder for backups; by default under the data folder.
        public string Path { get; set; } = "";

        // Protected credentials need the key ring of the instance.
        internal string? KeyDirectory { get; set; }

        // Set, a backup keeps the key ring only encrypted with it; a restore asks for it.
        public string? Passphrase { get; set; }
    }
}
