namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How a release is fetched: from Usenet, as a torrent, or straight from a catalog source.
    public enum ReleaseProtocol : byte
    {
        Usenet = 1,
        Torrent = 2,
        Direct = 3
    }
}
