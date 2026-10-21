using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // The titles of a mass change and whether they become monitored.
    internal sealed class WorkMonitoringData : BaseData
    {
        public WorkMonitoringData(long[] workIds, bool isMonitored)
        {
            WorkIds = workIds;
            IsMonitored = isMonitored;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsMonitored))]
        public bool IsMonitored { get; }
    }

    // The titles of a mass change and the profile or library their targets take; none lets the type's default apply.
    internal sealed class WorkTargetChoiceData : BaseData
    {
        public WorkTargetChoiceData(long[] workIds, long? acquisitionProfileId, long? libraryRootId)
        {
            WorkIds = workIds;
            AcquisitionProfileId = acquisitionProfileId;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkIds))]
        public long[] WorkIds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }
    }

    // A title's targets before a mass change: how many there are, how many already have the wanted value and whether the choice suits the title.
    internal sealed class WorkTargetStateData : BaseData
    {
        public WorkTargetStateData(long id, string canonicalTitle, MediaType mediaTypeId, int targets, int matching, bool fits)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            MediaTypeId = mediaTypeId;
            Targets = targets;
            Matching = matching;
            Fits = fits;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TargetCount))]
        public int Targets { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TargetCount))]
        public int Matching { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.Fits))]
        public bool Fits { get; }
    }

    // A chosen title with the target a search would run for; none when it is not wanted.
    internal sealed class WorkSearchTargetData : BaseData
    {
        public WorkSearchTargetData(long id, string canonicalTitle, MediaType mediaTypeId, long? wantedItemId)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            MediaTypeId = mediaTypeId;
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }
    }

    // What happened to one title of a mass change.
    internal sealed class WorkOutcomeData : BaseData
    {
        public WorkOutcomeData(long id, string canonicalTitle, string outcomeKey)
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
