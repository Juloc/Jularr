using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;

namespace Jularr.Frontend.Modules.Reader
{
    // Transient navigation only; semantic reading progress remains service-owned.
    public sealed class ReaderPositionData : BaseData
    {
        [DataField(typeof(ReaderPositionFields), nameof(ReaderPositionFields.Position))]
        public int Position { get; } = 1;
    }

    public static class ReaderPositionFields
    {
        public static readonly IntField Position = new() { Minimum = 1, Maximum = int.MaxValue };
    }
}
