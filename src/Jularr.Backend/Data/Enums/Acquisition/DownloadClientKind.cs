namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Who carries out a download, as the Downloads page shows it: every SABnzbd and qBittorrent is a client of its own; Jularr's news servers, its torrent engine and its direct sources are one client each.
    public enum DownloadClientKind : byte
    {
        Usenet = 1,
        Sabnzbd = 2,
        Qbittorrent = 3,
        Torrent = 4,
        Direct = 5
    }
}
