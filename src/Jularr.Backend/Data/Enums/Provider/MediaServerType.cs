namespace Jularr.Backend.Data.Enums.Provider
{
    // Media servers sharing watch progress; the provider key is the lower-case name.
    public enum MediaServerType : byte
    {
        Jellyfin = 1,
        Emby = 2
    }
}
