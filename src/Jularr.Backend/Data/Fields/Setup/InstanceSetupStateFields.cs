using Jularr.Backend.Data.Enums.Instance;

namespace Jularr.Backend.Data.Fields.Setup
{
    public static class InstanceSetupStateFields
    {
        public static readonly LongField Id = new() { Minimum = 1, Maximum = 1 };
        public static readonly DateTimeField OwnerCreatedAt = new() { Required = false };
        public static readonly EnumField SetupStepTypeId = new(typeof(SetupStepType));
        public static readonly DateTimeField CompletedAt = new() { Required = false };
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
