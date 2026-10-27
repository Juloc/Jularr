using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadCatalogCacheParametersV1 : BaseParameters
    {
    }

    // The catalog choices of the whole instance: how long unused details and catalog rows are kept, and whether every Discover list is detailed in advance.
    public sealed class CatalogCacheV1 : BaseResponse
    {
        public CatalogCacheV1(int keepDays, bool detailsDiscoverAhead)
        {
            KeepDays = keepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.DetailsDiscoverAhead))]
        public bool DetailsDiscoverAhead { get; }
    }

    public sealed class UpdateCatalogCacheDataV1 : BaseData
    {
        public UpdateCatalogCacheDataV1(int keepDays, bool detailsDiscoverAhead)
        {
            KeepDays = keepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.DetailsDiscoverAhead))]
        public bool DetailsDiscoverAhead { get; }
    }
}
