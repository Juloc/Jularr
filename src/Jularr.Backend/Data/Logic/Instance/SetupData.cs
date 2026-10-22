using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class SetupStateData : BaseData
    {
        public SetupStateData(SetupStepType setupStepTypeId, SetupStartType setupStartTypeId, bool isCompleted, long version)
        {
            SetupStepTypeId = setupStepTypeId;
            SetupStartTypeId = setupStartTypeId;
            IsCompleted = isCompleted;
            Version = version;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStepTypeId))]
        public SetupStepType SetupStepTypeId { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStartTypeId))]
        public SetupStartType SetupStartTypeId { get; }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.IsCompleted))]
        public bool IsCompleted { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }
    }

    internal sealed class SetupStepData : BaseData
    {
        public SetupStepData(SetupStepType setupStepTypeId)
        {
            SetupStepTypeId = setupStepTypeId;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStepTypeId))]
        public SetupStepType SetupStepTypeId { get; }
    }

    internal sealed class SetupStartData : BaseData
    {
        public SetupStartData(SetupStartType setupStartTypeId)
        {
            SetupStartTypeId = setupStartTypeId;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.SetupStartTypeId))]
        public SetupStartType SetupStartTypeId { get; }
    }

    internal sealed class SetupVersionData : BaseData
    {
        public SetupVersionData(long version)
        {
            Version = version;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }
    }

    internal sealed class SetupCheckData : BaseData
    {
        public SetupCheckData(string checkKey, bool isBlocking)
        {
            CheckKey = checkKey;
            IsBlocking = isBlocking;
        }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.CheckKey))]
        public string CheckKey { get; }

        [DataField(typeof(SetupProgressFields), nameof(SetupProgressFields.IsBlocking))]
        public bool IsBlocking { get; }
    }
}
