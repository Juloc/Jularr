namespace Jularr.Backend.Data.Enums.Provider
{
    // The servers whose libraries or watch progress the provider sync reads.
    public enum ProviderSyncType : byte
    {
        Plex = 1,
        Jellyfin = 2,
        Emby = 3
    }
}
