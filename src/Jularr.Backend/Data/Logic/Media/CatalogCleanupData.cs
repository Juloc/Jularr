using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // How many catalog titles one cleanup statement may change, so no statement holds the Works table for long, and what the instance's caching tier keeps:
    // Discover's lists (above the lean tier) and the catalogs kept whole (full tier).
    internal sealed class CatalogCleanupData : BaseData
    {
        public CatalogCleanupData(int limit, bool keepsDiscoverLists, string[] wholeCatalogs)
        {
            Limit = limit;
            KeepsDiscoverLists = keepsDiscoverLists;
            WholeCatalogs = wholeCatalogs;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.KeepsDiscoverLists))]
        public bool KeepsDiscoverLists { get; }

        [DataField(typeof(ExternalFeedFields), nameof(ExternalFeedFields.WholeCatalogs))]
        public string[] WholeCatalogs { get; }
    }
}
