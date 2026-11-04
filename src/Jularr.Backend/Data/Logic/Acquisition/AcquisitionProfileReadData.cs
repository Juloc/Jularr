using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionProfileReadData : BaseData
    {
        public AcquisitionProfileReadData(long id, string name, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string definition, int wantedCount, long version)
        {
            Id = id;
            Name = name;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            Definition = definition;
            WantedCount = wantedCount;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKinds))]
        public AcquisitionMediaKind[] MediaKinds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DefaultFor))]
        public AcquisitionMediaKind[] DefaultFor { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int WantedCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

}
