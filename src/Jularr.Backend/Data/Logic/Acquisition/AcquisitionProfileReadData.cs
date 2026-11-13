using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionProfileReadData : BaseData
    {
        public AcquisitionProfileReadData(long id, string name, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string definition, int wantedCount, long version, int ruleCount = 0,
            int openRequestCount = 0)
        {
            Id = id;
            Name = name;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            Definition = definition;
            WantedCount = wantedCount;
            Version = version;
            RuleCount = ruleCount;
            OpenRequestCount = openRequestCount;
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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int RuleCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int OpenRequestCount { get; }
    }

    // A video profile with every custom format and rating a search of its first media type would read.
    internal sealed class ReleaseTestProfileData : BaseData
    {
        public ReleaseTestProfileData(long id, string name, AcquisitionMediaKind mediaKind, string definition, string formats, string releaseGroupRatings)
        {
            Id = id;
            Name = name;
            MediaKind = mediaKind;
            Definition = definition;
            Formats = formats;
            ReleaseGroupRatings = releaseGroupRatings;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKind))]
        public AcquisitionMediaKind MediaKind { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Formats { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string ReleaseGroupRatings { get; }
    }
}
