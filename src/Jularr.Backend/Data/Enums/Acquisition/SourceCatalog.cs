namespace Jularr.Backend.Data.Enums.Acquisition
{
    public enum SourceAvailability : byte
    {
        Ready = 1,
        Account = 2,
        Planned = 3,
        ReferenceOnly = 4
    }

    [Flags]
    public enum SourceCapability : byte
    {
        None = 0,
        Metadata = 1,
        Search = 2,
        Download = 4,
        Monitoring = 8,
        Link = 16,
        Subtitles = 32
    }

    public enum SourceGroup : byte
    {
        Books = 1,
        Novels = 2,
        Manga = 3,
        Audiobooks = 4,
        Acquisition = 5,
        Music = 6,
        Games = 7,
        Subtitles = 8
    }

    // One suggested source; ready entries map to a connection type or a metadata provider.
    public sealed class SourceCatalogEntry
    {
        public SourceCatalogEntry(string key, string name, SourceGroup group, SourceCapability capabilities, SourceAvailability availability, AcquisitionConnectionType? connectionType = null,
            string? providerKey = null, string? defaultBaseUrl = null)
        {
            Key = key;
            Name = name;
            Group = group;
            Capabilities = capabilities;
            Availability = availability;
            ConnectionType = connectionType;
            ProviderKey = providerKey;
            DefaultBaseUrl = defaultBaseUrl;
        }

        public string Key { get; }
        public string Name { get; }
        public SourceGroup Group { get; }
        public SourceCapability Capabilities { get; }
        public SourceAvailability Availability { get; }
        public AcquisitionConnectionType? ConnectionType { get; }
        public string? ProviderKey { get; }
        public string? DefaultBaseUrl { get; }
    }

    // What Jularr can honestly do with each source today; no entry claims downloads it does not support.
    public static class SourceCatalog
    {
        private const SourceCapability MetadataSearch = SourceCapability.Metadata | SourceCapability.Search;
        private const SourceCapability SearchDownload = SourceCapability.Search | SourceCapability.Download;

        public static IReadOnlyList<SourceCatalogEntry> Entries { get; } =
        [
            new("openlibrary", "Open Library", SourceGroup.Books, MetadataSearch, SourceAvailability.Ready, providerKey: "openlibrary"),
            new("gutenberg", "Project Gutenberg (Gutendex)", SourceGroup.Books, SearchDownload, SourceAvailability.Ready, AcquisitionConnectionType.Gutenberg, defaultBaseUrl: "https://gutendex.com"),
            new("standardebooks", "Standard Ebooks (OPDS)", SourceGroup.Books, SearchDownload, SourceAvailability.Account, AcquisitionConnectionType.Opds, defaultBaseUrl: "https://standardebooks.org/feeds/opds"),
            new("opds", "OPDS catalog", SourceGroup.Books, SearchDownload, SourceAvailability.Ready, AcquisitionConnectionType.Opds),
            new("calibreweb", "Calibre / Calibre-Web (OPDS)", SourceGroup.Books, SearchDownload, SourceAvailability.Account, AcquisitionConnectionType.Opds),
            new("annasarchive", "Anna's Archive (optional member key)", SourceGroup.Books, SearchDownload, SourceAvailability.Account, AcquisitionConnectionType.AnnasArchive, defaultBaseUrl: "https://annas-archive.gl"),
            new("nyaa", "Nyaa (torrent index)", SourceGroup.Novels, SourceCapability.Search, SourceAvailability.Planned, AcquisitionConnectionType.Nyaa, defaultBaseUrl: "https://nyaa.si"),
            new("googlebooks", "Google Books", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("hardcover", "Hardcover (book series)", SourceGroup.Books, SourceCapability.Metadata, SourceAvailability.Account, providerKey: "hardcover"),
            new("wikisource", "Wikisource", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("internetarchive", "Internet Archive", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("nyt", "NYT Best Sellers", SourceGroup.Books, SourceCapability.Metadata, SourceAvailability.Planned),
            // Narō's API finds the web novels (provider), the novel pages give the chapters as one EPUB (connection).
            new("narou", "Shōsetsuka ni Narō", SourceGroup.Novels, MetadataSearch | SourceCapability.Download, SourceAvailability.Ready, AcquisitionConnectionType.Narou, providerKey: "syosetu",
                defaultBaseUrl: "https://ncode.syosetu.com"),
            new("kakuyomu", "Kakuyomu", SourceGroup.Novels, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("royalroad", "Royal Road", SourceGroup.Novels, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("scribblehub", "Scribble Hub", SourceGroup.Novels, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("novelupdates", "Novel Updates", SourceGroup.Novels, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("ao3", "Archive of Our Own", SourceGroup.Novels, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("aozora", "Aozora Bunko", SourceGroup.Novels, MetadataSearch, SourceAvailability.Planned),
            new("natively", "Natively", SourceGroup.Novels, SourceCapability.Metadata | SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("webnovel", "WebNovel", SourceGroup.Novels, SourceCapability.Metadata | SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("jnovelclub", "J-Novel Club", SourceGroup.Novels, SourceCapability.Metadata | SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("bookwalker", "BOOK☆WALKER", SourceGroup.Novels, SourceCapability.Metadata | SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("justlightnovels", "JustLightNovels", SourceGroup.Novels, SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("empirenovel", "EmpireNovel", SourceGroup.Novels, SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("novelping", "NovelPing", SourceGroup.Novels, SourceCapability.Link, SourceAvailability.ReferenceOnly),
            new("anilist", "AniList", SourceGroup.Manga, MetadataSearch, SourceAvailability.Ready, providerKey: "anilist"),
            new("musicbrainz", "MusicBrainz", SourceGroup.Music, MetadataSearch, SourceAvailability.Ready, providerKey: "musicbrainz"),
            new("acoustid", "AcoustID (untagged music by fingerprint)", SourceGroup.Music, SourceCapability.Metadata, SourceAvailability.Account, providerKey: "acoustid"),
            new("igdb", "IGDB (Twitch application)", SourceGroup.Games, MetadataSearch, SourceAvailability.Account, providerKey: "igdb"),
            new("screenscraper", "ScreenScraper", SourceGroup.Games, MetadataSearch, SourceAvailability.Planned),
            new("libretro", "Libretro database", SourceGroup.Games, MetadataSearch, SourceAvailability.Planned),
            new("steamgriddb", "SteamGridDB", SourceGroup.Games, MetadataSearch, SourceAvailability.Planned),
            new("pcgamingwiki", "PCGamingWiki", SourceGroup.Games, MetadataSearch, SourceAvailability.Planned),
            new("mangadex", "MangaDex", SourceGroup.Manga, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("librivox", "LibriVox", SourceGroup.Audiobooks, MetadataSearch, SourceAvailability.Planned),
            new("newznab", "Newznab indexer", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab),
            // Well-known Newznab indexers; each needs its own account key.
            new("nzbgeek", "NZBgeek", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://api.nzbgeek.info"),
            new("drunkenslug", "DrunkenSlug", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://drunkenslug.com"),
            new("nzbplanet", "NZBPlanet", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://api.nzbplanet.net"),
            new("nzbsu", "Nzb.su", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://api.nzb.su"),
            new("nzbfinder", "NZBFinder", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://nzbfinder.ws"),
            new("althub", "altHUB", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab, defaultBaseUrl: "https://api.althub.co.za"),
            new("sabnzbd", "SABnzbd", SourceGroup.Acquisition, SourceCapability.Download, SourceAvailability.Account, AcquisitionConnectionType.Sabnzbd),
            new("qbittorrent", "qBittorrent (torrent client)", SourceGroup.Acquisition, SourceCapability.Download, SourceAvailability.Account, AcquisitionConnectionType.Qbittorrent),
            new("native", "Jularr usenet downloader", SourceGroup.Acquisition, SourceCapability.Download, SourceAvailability.Planned),
            new("opensubtitles", "OpenSubtitles.com", SourceGroup.Subtitles, SourceCapability.Subtitles, SourceAvailability.Account, AcquisitionConnectionType.OpenSubtitles,
                defaultBaseUrl: "https://api.opensubtitles.com/api/v1"),
            new("subdl", "SubDL", SourceGroup.Subtitles, SourceCapability.Subtitles, SourceAvailability.Account, AcquisitionConnectionType.SubDl, defaultBaseUrl: "https://api.subdl.com/api/v1"),
            new("podnapisi", "Podnapisi", SourceGroup.Subtitles, SourceCapability.Subtitles, SourceAvailability.Planned),
            new("gestdown", "Gestdown (Addic7ed)", SourceGroup.Subtitles, SourceCapability.Subtitles, SourceAvailability.Planned),
            new("jimaku", "Jimaku (anime)", SourceGroup.Subtitles, SourceCapability.Subtitles, SourceAvailability.Planned)
        ];
    }
}
