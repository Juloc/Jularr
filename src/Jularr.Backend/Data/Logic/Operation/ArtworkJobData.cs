using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Operation
{
    internal sealed class ArtworkWorkQueueData : BaseData
    {
        public ArtworkWorkQueueData(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class ArtworkRootQueueData : BaseData
    {
        public ArtworkRootQueueData(long libraryRootId)
        {
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }
    }

    internal sealed class ArtworkJobData : BaseData
    {
        public ArtworkJobData(long id)
        {
            Id = id;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }
    }

    internal sealed class ArtworkJobParameters : BaseData
    {
        public ArtworkJobParameters(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }
}
