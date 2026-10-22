using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    // The counts of the Dashboard's tiles.
    internal sealed class DashboardTotalsData : BaseData
    {
        public DashboardTotalsData(long works, long files, long openRequests)
        {
            Works = works;
            Files = files;
            OpenRequests = openRequests;
        }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long Works { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long Files { get; }

        [DataField(typeof(DashboardFields), nameof(DashboardFields.Count))]
        public long OpenRequests { get; }
    }
}
