using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class SetupProgressParametersV1 : BaseParameters
    {
    }

    // Where the wizard resumes and what still blocks or deserves a look before it finishes.
    public sealed class SetupProgressV1 : BaseResponse
    {
        public SetupProgressV1(SetupStepType setupStepTypeId, bool isCompleted, long version, string[] blockers, string[] warnings)
        {
            SetupStepTypeId = setupStepTypeId;
            IsCompleted = isCompleted;
            Version = version;
            Blockers = blockers;
            Warnings = warnings;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStepTypeId))]
        public SetupStepType SetupStepTypeId { get; }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.IsCompleted))]
        public bool IsCompleted { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.Blockers))]
        public string[] Blockers { get; }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.Warnings))]
        public string[] Warnings { get; }
    }

    // The wizard resumes at the furthest step reached.
    public sealed class UpdateSetupProgressDataV1 : BaseData
    {
        public UpdateSetupProgressDataV1(SetupStepType setupStepTypeId)
        {
            SetupStepTypeId = setupStepTypeId;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStepTypeId))]
        public SetupStepType SetupStepTypeId { get; }
    }

    public sealed class CompleteSetupDataV1 : BaseData
    {
        public CompleteSetupDataV1(long version)
        {
            Version = version;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }
    }
}
