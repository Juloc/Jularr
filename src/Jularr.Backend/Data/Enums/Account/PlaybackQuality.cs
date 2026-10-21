namespace Jularr.Backend.Data.Enums.Account
{
    // How a device plays: Automatic leaves the choice to the server, Original asks for the file as it is, High and Saver cap what is converted.
    public enum PlaybackQuality : byte
    {
        Automatic = 1,
        Original = 2,
        High = 3,
        Saver = 4
    }
}
