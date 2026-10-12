using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Public.Instance.V1
{
    public sealed class ReadInstancePresentationParametersV1 : BaseParameters
    {
    }

    public sealed class InstancePresentationDetailsV1 : BaseResponse
    {
        public InstancePresentationDetailsV1(string name, long uiLocaleId)
        {
            Name = name;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }
    }
}
