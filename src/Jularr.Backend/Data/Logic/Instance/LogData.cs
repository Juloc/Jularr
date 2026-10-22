using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    // How many of the newest entries a source gives: the page's end.
    internal sealed class LogTakeData : BaseData
    {
        public LogTakeData(int take)
        {
            Take = take;
        }

        [DataField(typeof(LogFields), nameof(LogFields.Take))]
        public int Take { get; }
    }
}
