using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.System.V1
{
    public sealed class ReadDashboardParametersV1 : BaseParameters
    {
    }

    // The figures of the Administration's first page; the downloads, the health check and the load come from their own services.
    public sealed class DashboardV1 : BaseResponse
    {
        public DashboardV1(long works, long files, long openRequests, BaseDataList<DashboardStorageV1> storage)
        {
            Works = works;
            Files = files;
            OpenRequests = openRequests;
            Storage = storage;
        }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long Works { get; }

        // Files that are there; one that is missing is not counted.
        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long Files { get; }

        // Requests that wait for an administrator's decision.
        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long OpenRequests { get; }

        // Every connection that holds a library.
        public BaseDataList<DashboardStorageV1> Storage { get; }
    }

    // One storage connection: what its volume holds and how much of it Jularr's media take, by kind.
    public sealed class DashboardStorageV1 : BaseResponse
    {
        public DashboardStorageV1(long storageMountId, string name, long? totalBytes, long? freeBytes, long videoBytes, long bookBytes, long musicBytes, long gameBytes)
        {
            StorageMountId = storageMountId;
            Name = name;
            TotalBytes = totalBytes;
            FreeBytes = freeBytes;
            VideoBytes = videoBytes;
            BookBytes = bookBytes;
            MusicBytes = musicBytes;
            GameBytes = gameBytes;
        }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Name))]
        public string Name { get; }

        // Unknown until the connection has been looked at once.
        [DataField(typeof(DashboardFields), nameof(DashboardFields.OptionalBytes))]
        public long? TotalBytes { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.OptionalBytes))]
        public long? FreeBytes { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Bytes))]
        public long VideoBytes { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Bytes))]
        public long BookBytes { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Bytes))]
        public long MusicBytes { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Bytes))]
        public long GameBytes { get; }
    }
}
