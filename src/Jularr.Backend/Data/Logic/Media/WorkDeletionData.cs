using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // What stands in the way of deleting a title and which files it owns.
    internal sealed class WorkDeletionFactsData : BaseData
    {
        public WorkDeletionFactsData(long[] activeDownloadIds, long[] storedFileIds)
        {
            ActiveDownloadIds = activeDownloadIds;
            StoredFileIds = storedFileIds;
        }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.ActiveDownloadIds))]
        public long[] ActiveDownloadIds { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Ids))]
        public long[] StoredFileIds { get; }
    }

    internal sealed class WorkDeletionData : BaseData
    {
        public WorkDeletionData(long workId, bool addsExclusion)
        {
            WorkId = workId;
            AddsExclusion = addsExclusion;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.AddsExclusion))]
        public bool AddsExclusion { get; }
    }

    internal sealed class ExclusionIdentityData : BaseData
    {
        public ExclusionIdentityData(string @namespace, string value)
        {
            Namespace = @namespace;
            Value = value;
        }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.Value))]
        public string Value { get; }
    }
}
