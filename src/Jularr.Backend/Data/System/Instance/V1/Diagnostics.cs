using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.System.Instance.V1
{
    public sealed class DiagnosticsStateParametersV1 : BaseParameters
    {
    }

    public sealed class DiagnosticsStateV1 : BaseResponse
    {
        public DiagnosticsStateV1(bool isEnabled)
        {
            IsEnabled = isEnabled;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }
    }
}
