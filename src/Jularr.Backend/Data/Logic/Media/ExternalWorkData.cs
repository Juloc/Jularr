using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // One provider title as Discover shows it.
    internal sealed class ExternalWorkData : BaseData
    {
        public ExternalWorkData(string @namespace, string value, string title, string? originalTitle, int? releaseYear, string? overview, string? creator, string? imageUrl,
            string? backdropUrl, DateTime fetchedAt)
        {
            Namespace = @namespace;
            Value = value;
            Title = title;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            Overview = overview;
            Creator = creator;
            ImageUrl = imageUrl;
            BackdropUrl = backdropUrl;
            FetchedAt = fetchedAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Title))]
        public string Title { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.OriginalTitle))]
        public string? OriginalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Creator))]
        public string? Creator { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ImageUrl))]
        public string? ImageUrl { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.BackdropUrl))]
        public string? BackdropUrl { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.FetchedAt))]
        public DateTime FetchedAt { get; }
    }

    // A catalog's list of one type: Namespace, type and subtype name it, trending ones are ranked in their order.
    internal sealed class ExternalWorkListData : BaseData
    {
        public ExternalWorkListData(string @namespace, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            Namespace = @namespace;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    internal sealed class ExternalWorkBatchData : BaseData
    {
        public ExternalWorkBatchData(ExternalWorkListData list, IReadOnlyList<ExternalWorkData> works, bool isTrending)
        {
            Namespace = list.Namespace;
            MediaTypeId = (short)list.MediaTypeId;
            MediaSubtypeId = list.MediaSubtypeId is { } subtype ? (short)subtype : null;
            IsTrending = isTrending;
            Values = works.Select(work => work.Value).ToArray();
            Titles = works.Select(work => work.Title).ToArray();
            OriginalTitles = works.Select(work => work.OriginalTitle).ToArray();
            ReleaseYears = works.Select(work => work.ReleaseYear ?? 0).ToArray();
            Overviews = works.Select(work => work.Overview).ToArray();
            Creators = works.Select(work => work.Creator).ToArray();
            ImageUrls = works.Select(work => work.ImageUrl).ToArray();
            BackdropUrls = works.Select(work => work.BackdropUrl).ToArray();
        }

        public string Namespace { get; }
        public short MediaTypeId { get; }
        public short? MediaSubtypeId { get; }
        public bool IsTrending { get; }
        public string[] Values { get; }
        public string[] Titles { get; }
        public string?[] OriginalTitles { get; }
        // Arrays carry no NULL numbers; 0 stands for an unknown year.
        public int[] ReleaseYears { get; }
        public string?[] Overviews { get; }
        public string?[] Creators { get; }
        public string?[] ImageUrls { get; }
        public string?[] BackdropUrls { get; }
    }

    // How far the full sync of one catalog has come.
    internal sealed class ExternalWorkSyncData : BaseData
    {
        public ExternalWorkSyncData(string @namespace, DateOnly? listedOn, DateTime? changesReadAt)
        {
            Namespace = @namespace;
            ListedOn = listedOn;
            ChangesReadAt = changesReadAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ListedOn))]
        public DateOnly? ListedOn { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ChangesReadAt))]
        public DateTime? ChangesReadAt { get; }
    }

    // The next entries a full sync reads, most popular first.
    internal sealed class ExternalWorkQueueData : BaseData
    {
        public ExternalWorkQueueData(string @namespace, int limit)
        {
            Namespace = @namespace;
            Limit = limit;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }
    }

    // Many titles of one catalog list as JSON rows (a provider's id list, or its entries just read) and the ones it no longer has.
    internal sealed class ExternalWorkEntriesData : BaseData
    {
        public ExternalWorkEntriesData(ExternalWorkListData list, string entries, string[]? gone = null)
        {
            Namespace = list.Namespace;
            MediaTypeId = (short)list.MediaTypeId;
            MediaSubtypeId = list.MediaSubtypeId is { } subtype ? (short)subtype : null;
            Entries = entries;
            Gone = gone ?? [];
        }

        public string Namespace { get; }
        public short MediaTypeId { get; }
        public short? MediaSubtypeId { get; }
        public string Entries { get; }
        public string[] Gone { get; }
    }

    internal sealed class ExternalWorkValueData : BaseData
    {
        public ExternalWorkValueData(string value)
        {
            Value = value;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }
    }

    // Values of one catalog, such as the entries a provider changed.
    internal sealed class ExternalWorkValuesData : BaseData
    {
        public ExternalWorkValuesData(string @namespace, string[] values)
        {
            Namespace = @namespace;
            Values = values;
        }

        public string Namespace { get; }
        public string[] Values { get; }
    }
}
