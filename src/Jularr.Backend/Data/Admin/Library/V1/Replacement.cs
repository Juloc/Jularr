using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class ReplacementSettingsParametersV1 : BaseParameters
    {
    }

    // What an import does with the earlier file of the same film, episode or volume.
    public sealed class ReplacementSettingsV1 : BaseResponse
    {
        public ReplacementSettingsV1(bool keepsReleases, bool recyclesInLibraries, long? recycleStorageMountId, string? recyclePath, int recycleDays, long version)
        {
            KeepsReleases = keepsReleases;
            RecyclesInLibraries = recyclesInLibraries;
            RecycleStorageMountId = recycleStorageMountId;
            RecyclePath = recyclePath;
            RecycleDays = recycleDays;
            Version = version;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.KeepsReleases))]
        public bool KeepsReleases { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclesInLibraries))]
        public bool RecyclesInLibraries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleStorageMountId))]
        public long? RecycleStorageMountId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclePath))]
        public string? RecyclePath { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleDays))]
        public int RecycleDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateReplacementSettingsDataV1 : BaseData
    {
        public UpdateReplacementSettingsDataV1(bool keepsReleases, bool recyclesInLibraries, long? recycleStorageMountId, string? recyclePath, int recycleDays, long version)
        {
            KeepsReleases = keepsReleases;
            RecyclesInLibraries = recyclesInLibraries;
            RecycleStorageMountId = recycleStorageMountId;
            RecyclePath = recyclePath;
            RecycleDays = recycleDays;
            Version = version;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.KeepsReleases))]
        public bool KeepsReleases { get; }

        // Simple mode: a recycle folder inside each library; otherwise the folder below, or deletion without one.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclesInLibraries))]
        public bool RecyclesInLibraries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleStorageMountId))]
        public long? RecycleStorageMountId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclePath))]
        public string? RecyclePath { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleDays))]
        public int RecycleDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
