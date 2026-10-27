using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // How many catalog titles one cleanup statement may change, so no statement holds the Works table for long.
    internal sealed class CatalogCleanupData : BaseData
    {
        public CatalogCleanupData(int limit)
        {
            Limit = limit;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }
    }
}
