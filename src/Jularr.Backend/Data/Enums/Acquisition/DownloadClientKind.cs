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

    // What each client can be told: the Downloads page offers exactly these, and the services leave a download alone whose client cannot do it.
    public static class DownloadClientKinds
    {
        // SABnzbd's four priorities, and Jularr's own downloaders, which take their jobs in that order; qBittorrent has no priorities of its own.
        public static bool HasPriorities(DownloadClientKind kind) => kind != DownloadClientKind.Qbittorrent;

        // A download is put anywhere in the queue; qBittorrent only puts one first.
        public static bool Reorders(DownloadClientKind kind) => kind != DownloadClientKind.Qbittorrent;

        // SABnzbd and qBittorrent keep their own limit; Jularr's Usenet and torrent downloaders hold back their sessions. Direct sources set their own pace.
        public static bool LimitsSpeed(DownloadClientKind kind) => kind != DownloadClientKind.Direct;

        // A download added by hand: an NZB address for Usenet clients, a torrent file address or a magnet for torrent clients; direct sources need their own search.
        public static bool AddsNzb(DownloadClientKind kind) => kind is DownloadClientKind.Usenet or DownloadClientKind.Sabnzbd;

        public static bool AddsTorrent(DownloadClientKind kind) => kind is DownloadClientKind.Qbittorrent or DownloadClientKind.Torrent;

        // The client of a connection type.
        public static DownloadClientKind? Of(AcquisitionConnectionType type) => type switch
        {
            AcquisitionConnectionType.Usenet => DownloadClientKind.Usenet,
            AcquisitionConnectionType.Sabnzbd => DownloadClientKind.Sabnzbd,
            AcquisitionConnectionType.Qbittorrent => DownloadClientKind.Qbittorrent,
            AcquisitionConnectionType.Torrent => DownloadClientKind.Torrent,
            AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive or AcquisitionConnectionType.Narou => DownloadClientKind.Direct,
            _ => null
        };
    }
}
