using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class WorksBulkParametersV1 : BaseParameters
    {
    }

    // The mass editor's actions: each takes the chosen titles, up to a bounded batch, and reports what happened to every one of them.
    public sealed class UpdateWorksMonitoringDataV1 : BaseData
    {
        public UpdateWorksMonitoringDataV1(long[] workIds, bool isMonitored)
        {
            WorkIds = workIds;
            IsMonitored = isMonitored;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsMonitored))]
        public bool IsMonitored { get; }
    }

    // Without a profile a title uses the default one of its type again.
    public sealed class UpdateWorksProfileDataV1 : BaseData
    {
        public UpdateWorksProfileDataV1(long[] workIds, long? acquisitionProfileId)
        {
            WorkIds = workIds;
            AcquisitionProfileId = acquisitionProfileId;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }
    }

    // The library new files of the titles go to; files already filed stay where they are. Without one the type's first writable library applies.
    public sealed class UpdateWorksLibraryDataV1 : BaseData
    {
        public UpdateWorksLibraryDataV1(long[] workIds, long? libraryRootId)
        {
            WorkIds = workIds;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }
    }

    // Titles whose action reaches providers, indexers or files take a smaller batch.
    public sealed class HeavyWorksDataV1 : BaseData
    {
        public HeavyWorksDataV1(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.HeavyWorkIds))]
        public long[] WorkIds { get; }
    }

    // Sonarr's dialog for several titles: the files go too when asked, and an exclusion keeps a request from bringing a title back.
    public sealed class DeleteWorksDataV1 : BaseData
    {
        public DeleteWorksDataV1(long[] workIds, bool deletesFiles, bool addsExclusion)
        {
            WorkIds = workIds;
            DeletesFiles = deletesFiles;
            AddsExclusion = addsExclusion;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.HeavyWorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.DeletesFiles))]
        public bool DeletesFiles { get; }

        [DataField(typeof(WorkExclusionFields), nameof(WorkExclusionFields.AddsExclusion))]
        public bool AddsExclusion { get; }
    }

    // What happened to one chosen title: done, unchanged, or the reason it was left as it is.
    public sealed class WorkBulkOutcomeV1 : BaseResponse
    {
        public WorkBulkOutcomeV1(long id, string canonicalTitle, string outcomeKey)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            OutcomeKey = outcomeKey;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.OutcomeKey))]
        public string OutcomeKey { get; }
    }
}
