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
        Link = 16
    }

    public enum SourceGroup : byte
    {
        Books = 1,
        Novels = 2,
        Manga = 3,
        Audiobooks = 4,
        Acquisition = 5
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
            new("annasarchive", "Anna's Archive (member key)", SourceGroup.Books, SearchDownload, SourceAvailability.Account, AcquisitionConnectionType.AnnasArchive, defaultBaseUrl: "https://annas-archive.org"),
            new("googlebooks", "Google Books", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("hardcover", "Hardcover", SourceGroup.Books, MetadataSearch, SourceAvailability.Planned),
            new("wikisource", "Wikisource", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("internetarchive", "Internet Archive", SourceGroup.Books, MetadataSearch | SourceCapability.Link, SourceAvailability.Planned),
            new("nyt", "NYT Best Sellers", SourceGroup.Books, SourceCapability.Metadata, SourceAvailability.Planned),
            new("anilist", "AniList", SourceGroup.Novels, MetadataSearch, SourceAvailability.Planned),
            new("narou", "Shōsetsuka ni Narō", SourceGroup.Novels, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
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
            new("mangadex", "MangaDex", SourceGroup.Manga, MetadataSearch | SourceCapability.Monitoring, SourceAvailability.Planned),
            new("librivox", "LibriVox", SourceGroup.Audiobooks, MetadataSearch, SourceAvailability.Planned),
            new("newznab", "Newznab indexer", SourceGroup.Acquisition, SourceCapability.Search, SourceAvailability.Account, AcquisitionConnectionType.Newznab),
            new("sabnzbd", "SABnzbd", SourceGroup.Acquisition, SourceCapability.Download, SourceAvailability.Account, AcquisitionConnectionType.Sabnzbd),
            new("native", "Jularr usenet downloader", SourceGroup.Acquisition, SourceCapability.Download, SourceAvailability.Planned)
        ];
    }
}
