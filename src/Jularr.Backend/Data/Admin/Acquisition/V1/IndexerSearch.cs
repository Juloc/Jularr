using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class IndexerSearchParametersV1 : BaseParameters
    {
    }

    // A free search across the indexers, as Prowlarr's: the query with its tokens, the indexers (none: all) and categories (none: all), one page of each indexer.
    public sealed class IndexerSearchDataV1 : BaseData
    {
        public IndexerSearchDataV1(string? query, IndexerSearchType indexerSearchTypeId = IndexerSearchType.Search, long[]? indexerIds = null, int[]? categories = null, int limit = 100, int offset = 0)
        {
            Query = query;
            IndexerSearchTypeId = indexerSearchTypeId;
            IndexerIds = indexerIds ?? [];
            Categories = categories ?? [];
            Limit = limit;
            Offset = offset;
        }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Query))]
        public string? Query { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.IndexerSearchTypeId))]
        public IndexerSearchType IndexerSearchTypeId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.IndexerIds))]
        public long[] IndexerIds { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Categories))]
        public int[] Categories { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Limit))]
        public int Limit { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Offset))]
        public int Offset { get; }
    }

    // The releases of every indexer asked, and how each indexer answered.
    public sealed class IndexerSearchResultV1 : BaseResponse
    {
        public IndexerSearchResultV1(BaseDataList<IndexerSearchReleaseV1> releases, BaseDataList<IndexerSearchStatusV1> indexers)
        {
            Releases = releases;
            Indexers = indexers;
        }

        public BaseDataList<IndexerSearchReleaseV1> Releases { get; }
        public BaseDataList<IndexerSearchStatusV1> Indexers { get; }
    }

    // One indexer's answer: its number of releases and the time it took, or why it gave none (timeout, limit, failure).
    public sealed class IndexerSearchStatusV1 : BaseResponse
    {
        public IndexerSearchStatusV1(long id, string name, int? count, int elapsedMilliseconds, string? errorKey)
        {
            Id = id;
            Name = name;
            Count = count;
            ElapsedMilliseconds = elapsedMilliseconds;
            ErrorKey = errorKey;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Count))]
        public int? Count { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ElapsedMilliseconds))]
        public int ElapsedMilliseconds { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ErrorKey))]
        public string? ErrorKey { get; }
    }

    // A release as Prowlarr lists it, with what Jularr reads from its name (languages, quality, platform). ReleaseId names it for the grab and the download.
    public sealed class IndexerSearchReleaseV1 : BaseResponse
    {
        public IndexerSearchReleaseV1(string releaseId, string title, long indexerId, string indexerName, ReleaseProtocol releaseProtocolId, long? sizeBytes, DateTime? publishedAt,
            string? infoUrl, int[] categories, int? grabs, int? files, int? seeders, int? leechers, string[] indexerFlags, string? imdbId, string? tmdbId, string? tvdbId, string? tvMazeId,
            string[] languages, string? qualityKey, string? platform)
        {
            ReleaseId = releaseId;
            Title = title;
            IndexerId = indexerId;
            IndexerName = indexerName;
            ReleaseProtocolId = releaseProtocolId;
            SizeBytes = sizeBytes;
            PublishedAt = publishedAt;
            InfoUrl = infoUrl;
            Categories = categories;
            Grabs = grabs;
            Files = files;
            Seeders = seeders;
            Leechers = leechers;
            IndexerFlags = indexerFlags;
            ImdbId = imdbId;
            TmdbId = tmdbId;
            TvdbId = tvdbId;
            TvMazeId = tvMazeId;
            Languages = languages;
            QualityKey = qualityKey;
            Platform = platform;
        }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ReleaseId))]
        public string ReleaseId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long IndexerId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string IndexerName { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseProtocolId))]
        public ReleaseProtocol ReleaseProtocolId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SizeBytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.PublishedAt))]
        public DateTime? PublishedAt { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.InfoUrl))]
        public string? InfoUrl { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Categories))]
        public int[] Categories { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Count))]
        public int? Grabs { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Count))]
        public int? Files { get; }

        // Torrents only.
        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Count))]
        public int? Seeders { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Count))]
        public int? Leechers { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.IndexerFlags))]
        public string[] IndexerFlags { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ExternalId))]
        public string? ImdbId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ExternalId))]
        public string? TmdbId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ExternalId))]
        public string? TvdbId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ExternalId))]
        public string? TvMazeId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Languages))]
        public string[] Languages { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Platform))]
        public string? Platform { get; }
    }

    // An indexer the free search can ask, with the categories it offers.
    public sealed class IndexerSearchSourceV1 : BaseResponse
    {
        public IndexerSearchSourceV1(long id, string name, ReleaseProtocol releaseProtocolId, int[] categories)
        {
            Id = id;
            Name = name;
            ReleaseProtocolId = releaseProtocolId;
            Categories = categories;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseProtocolId))]
        public ReleaseProtocol ReleaseProtocolId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.Categories))]
        public int[] Categories { get; }
    }

    // A release of the last searches, named by its ReleaseId.
    public sealed class IndexerReleaseParametersV1 : BaseParameters
    {
        public IndexerReleaseParametersV1(string releaseId)
        {
            ReleaseId = releaseId;
        }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ReleaseId))]
        public string ReleaseId { get; }
    }

    // The .nzb or .torrent of a release, saved by the browser.
    public sealed class IndexerReleaseFileV1 : BaseResponse, IDownloadOutput
    {
        public IndexerReleaseFileV1(byte[] bytes, string fileName, string contentType)
        {
            Bytes = bytes;
            FileName = fileName;
            ContentType = contentType;
        }

        [JsonIgnore]
        public byte[] Bytes { get; }

        [JsonIgnore]
        public string FileName { get; }

        [JsonIgnore]
        public string ContentType { get; }
    }

    // The work a release is for: the wanted title its name fits, else none, and why (no title fits, or several).
    public sealed class IndexerReleaseWorkV1 : BaseResponse
    {
        public IndexerReleaseWorkV1(long? wantedItemId, string? title, int? releaseYear, MediaType? mediaTypeId, string? problemKey)
        {
            WantedItemId = wantedItemId;
            Title = title;
            ReleaseYear = releaseYear;
            MediaTypeId = mediaTypeId;
            ProblemKey = problemKey;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(IndexerSearchFields), nameof(IndexerSearchFields.ErrorKey))]
        public string? ProblemKey { get; }
    }

    // The wanted title a release is grabbed and imported for.
    public sealed class GrabIndexerReleaseDataV1 : BaseData
    {
        public GrabIndexerReleaseDataV1(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }
}
