namespace Jularr.Backend.Data.Enums.Account
{
    // How a device plays music: the original file, or converted to AAC of that many kilobits a second.
    public enum AudioQuality : byte
    {
        Original = 1,
        High = 2,
        Small = 3
    }
}
