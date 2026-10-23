using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // Which covers to look for: one Work's, or every Work's up to a limit.
    internal sealed class WorkCoverQueryData : BaseData
    {
        public WorkCoverQueryData(long? workId, int limit)
        {
            WorkId = workId;
            Limit = limit;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long? WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Limit))]
        public int Limit { get; }
    }

    // An image a provider names that is not stored yet.
    internal sealed class WorkCoverTargetData : BaseData
    {
        public WorkCoverTargetData(long workId, CoverKind coverKindId, long? workUnitId, string sourceUrl)
        {
            WorkId = workId;
            CoverKindId = coverKindId;
            WorkUnitId = workUnitId;
            SourceUrl = sourceUrl;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.CoverKindId))]
        public CoverKind CoverKindId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkUnitId))]
        public long? WorkUnitId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.SourceUrl))]
        public string SourceUrl { get; }
    }

    // A fetched image kept on disk.
    internal sealed class WorkCoverStoredData : BaseData
    {
        public WorkCoverStoredData(WorkCoverTargetData target, string contentType, string version, string localUrl, CoverFormat formatId)
        {
            WorkId = target.WorkId;
            CoverKindId = target.CoverKindId;
            WorkUnitId = target.WorkUnitId;
            SourceUrl = target.SourceUrl;
            ContentType = contentType;
            Version = version;
            LocalUrl = localUrl;
            FormatId = formatId;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.CoverKindId))]
        public CoverKind CoverKindId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkUnitId))]
        public long? WorkUnitId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.SourceUrl))]
        public string SourceUrl { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.ContentType))]
        public string ContentType { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Version))]
        public string Version { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.LocalUrl))]
        public string LocalUrl { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.FormatId))]
        public CoverFormat FormatId { get; }
    }

    // How many stored covers to take that are not yet kept in this format.
    internal sealed class WorkCoverConvertQueryData : BaseData
    {
        public WorkCoverConvertQueryData(CoverFormat formatId, int limit)
        {
            FormatId = formatId;
            Limit = limit;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.FormatId))]
        public CoverFormat FormatId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Limit))]
        public int Limit { get; }
    }

    // A stored cover whose widths are not kept in the Owner's format yet; its version tells the conversion whether the image was replaced meanwhile.
    internal sealed class WorkCoverConvertData : BaseData
    {
        public WorkCoverConvertData(long workId, CoverKind coverKindId, long? workUnitId, string contentType, string version)
        {
            WorkId = workId;
            CoverKindId = coverKindId;
            WorkUnitId = workUnitId;
            ContentType = contentType;
            Version = version;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.CoverKindId))]
        public CoverKind CoverKindId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkUnitId))]
        public long? WorkUnitId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.ContentType))]
        public string ContentType { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Version))]
        public string Version { get; }
    }

    // The format a stored cover's widths are now kept in.
    internal sealed class WorkCoverFormatData : BaseData
    {
        public WorkCoverFormatData(WorkCoverConvertData cover, CoverFormat formatId)
        {
            WorkId = cover.WorkId;
            CoverKindId = cover.CoverKindId;
            WorkUnitId = cover.WorkUnitId;
            Version = cover.Version;
            FormatId = formatId;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.CoverKindId))]
        public CoverKind CoverKindId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkUnitId))]
        public long? WorkUnitId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Version))]
        public string Version { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.FormatId))]
        public CoverFormat FormatId { get; }
    }

    // A stored cover as the sweep and the endpoint name it.
    internal sealed class WorkCoverRefData : BaseData
    {
        public WorkCoverRefData(long workId, CoverKind coverKindId, long? workUnitId)
        {
            WorkId = workId;
            CoverKindId = coverKindId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.CoverKindId))]
        public CoverKind CoverKindId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkUnitId))]
        public long? WorkUnitId { get; }
    }

    // What the endpoint needs to know about a stored cover.
    internal sealed class WorkCoverFileInfoData : BaseData
    {
        public WorkCoverFileInfoData(string contentType, string version)
        {
            ContentType = contentType;
            Version = version;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.ContentType))]
        public string ContentType { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Version))]
        public string Version { get; }
    }

    // The Owner's choices for the cache of images of titles that are not Works.
    internal sealed class CoverCacheSettingsData : BaseData
    {
        public CoverCacheSettingsData(int keepDays, int maximumMegabytes, CoverFormat formatId, long version)
        {
            KeepDays = keepDays;
            MaximumMegabytes = maximumMegabytes;
            FormatId = formatId;
            Version = version;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.MaximumMegabytes))]
        public int MaximumMegabytes { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.FormatId))]
        public CoverFormat FormatId { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Version))]
        public long Version { get; }
    }

    internal sealed class WorkCoverWorkIdsData : BaseData
    {
        public WorkCoverWorkIdsData(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkIds))]
        public long[] WorkIds { get; }
    }
}
