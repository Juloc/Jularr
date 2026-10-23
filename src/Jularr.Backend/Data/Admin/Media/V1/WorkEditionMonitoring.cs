using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class WorkEditionParametersV1 : BaseParameters
    {
        public WorkEditionParametersV1(long workId, long workEditionId)
        {
            WorkId = workId;
            WorkEditionId = workEditionId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GroupId))]
        public long WorkEditionId { get; }
    }

    // An edition of a book is wanted or not: releases in the language of an edition that is not wanted are left out.
    public sealed class UpdateWorkEditionDataV1 : BaseData
    {
        public UpdateWorkEditionDataV1(bool isMonitored)
        {
            IsMonitored = isMonitored;
        }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.IsMonitoredEdition))]
        public bool IsMonitored { get; }
    }
}
