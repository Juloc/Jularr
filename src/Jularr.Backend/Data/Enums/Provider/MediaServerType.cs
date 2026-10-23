namespace Jularr.Backend.Data.Enums.Provider
{
    // Media servers sharing watch progress.
    public enum MediaServerType : byte
    {
        Jellyfin = 1,
        Emby = 2
    }

    public static class MediaServerTypes
    {
        // The provider key is the lower-case name.
        public static string ProviderKey(this MediaServerType type) => type.ToString().ToLowerInvariant();

        public static MediaServerType? FromProviderKey(string key) =>
            Enum.GetValues<MediaServerType>().Cast<MediaServerType?>().FirstOrDefault(type => type!.Value.ProviderKey() == key);
    }
}
