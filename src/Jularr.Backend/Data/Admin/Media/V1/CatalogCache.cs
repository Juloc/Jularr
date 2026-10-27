using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadCatalogCacheParametersV1 : BaseParameters
    {
    }

    // One catalog's cache tier, the highest tier it can keep and its trending switch.
    public sealed class CatalogCacheProviderV1 : BaseData
    {
        public CatalogCacheProviderV1(string @namespace, int cacheTier, int maximumTier, bool detailsTrending)
        {
            Namespace = @namespace;
            CacheTier = cacheTier;
            MaximumTier = maximumTier;
            DetailsTrending = detailsTrending;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.CacheTier))]
        public int CacheTier { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.MaximumTier))]
        public int MaximumTier { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.DetailsTrending))]
        public bool DetailsTrending { get; }
    }

    public sealed class CatalogCacheV1 : BaseResponse
    {
        public CatalogCacheV1(int keepDays, BaseDataList<CatalogCacheProviderV1> providers)
        {
            KeepDays = keepDays;
            Providers = providers;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.KeepDays))]
        public int KeepDays { get; }

        public BaseDataList<CatalogCacheProviderV1> Providers { get; }
    }

    public sealed class UpdateCatalogCacheProviderDataV1 : BaseData
    {
        public UpdateCatalogCacheProviderDataV1(string @namespace, int cacheTier, bool detailsTrending)
        {
            Namespace = @namespace;
            CacheTier = cacheTier;
            DetailsTrending = detailsTrending;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.CacheTier))]
        public int CacheTier { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.DetailsTrending))]
        public bool DetailsTrending { get; }
    }

    public sealed class UpdateCatalogCacheDataV1 : BaseData
    {
        public UpdateCatalogCacheDataV1(int keepDays, BaseDataList<UpdateCatalogCacheProviderDataV1> providers)
        {
            KeepDays = keepDays;
            Providers = providers;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.KeepDays))]
        public int KeepDays { get; }

        public BaseDataList<UpdateCatalogCacheProviderDataV1> Providers { get; }
    }
}
