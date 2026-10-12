using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Wanted.V1
{
    public sealed class ReadWantedParametersV1 : BaseParameters
    {
        public ReadWantedParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class UpdateWantedParametersV1 : BaseParameters
    {
        public UpdateWantedParametersV1(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }

    public sealed class UpdateWantedDataV1 : BaseData
    {
        public UpdateWantedDataV1(bool isPaused, long version)
        {
            IsPaused = isPaused;
            Version = version;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }
    }

    public sealed class WantedDetailsV1 : BaseResponse
    {
        public WantedDetailsV1(long id, long workId, string canonicalTitle, bool isPaused, long version, string[] requestedLanguages)
        {
            Id = id;
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            IsPaused = isPaused;
            Version = version;
            RequestedLanguages = requestedLanguages;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.RequestedLanguages))]
        public string[] RequestedLanguages { get; }
    }
}
