namespace Jularr.Integrations.Data.Synology
{
    internal sealed class SynologyAccess(Uri baseAddress, string username, string password)
    {
        public Uri BaseAddress { get; } = baseAddress;
        public string Username { get; } = username;
        public string Password { get; } = password;
    }

    internal sealed class SynologyStatus(string? model, string? version, int? temperature, IReadOnlyList<SynologyVolume> volumes, IReadOnlyList<SynologyDisk> disks)
    {
        public string? Model { get; } = model;
        public string? Version { get; } = version;
        public int? Temperature { get; } = temperature;
        public IReadOnlyList<SynologyVolume> Volumes { get; } = volumes;
        public IReadOnlyList<SynologyDisk> Disks { get; } = disks;
    }

    internal sealed class SynologyVolume(string? name, string? status, long? totalBytes, long? usedBytes)
    {
        public string? Name { get; } = name;
        public string? Status { get; } = status;
        public long? TotalBytes { get; } = totalBytes;
        public long? UsedBytes { get; } = usedBytes;
    }

    internal sealed class SynologyDisk(string? name, string? model, string? status, int? temperature)
    {
        public string? Name { get; } = name;
        public string? Model { get; } = model;
        public string? Status { get; } = status;
        public int? Temperature { get; } = temperature;
    }

    internal sealed class SynologyException(string code) : Exception("The Synology NAS did not answer as expected.")
    {
        public string Code { get; } = code;
    }
}
