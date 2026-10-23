using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.User.Instance.V1
{
    public sealed class ReadInstanceCapabilitiesParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceCapabilitiesDetailsV1 : BaseResponse
    {
        public InstanceCapabilitiesDetailsV1(string[] permissions, string[] modules)
        {
            Permissions = permissions;
            Modules = modules;
        }

        // The caller's verified grants.
        [DataField(typeof(CapabilityFields), nameof(CapabilityFields.Permissions))]
        public string[] Permissions { get; }

        // Enabled modules that can work here.
        [DataField(typeof(CapabilityFields), nameof(CapabilityFields.Modules))]
        public string[] Modules { get; }
    }
}
