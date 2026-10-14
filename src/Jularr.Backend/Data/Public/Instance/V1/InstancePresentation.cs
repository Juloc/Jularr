using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Public.Instance.V1
{
    public sealed class ReadInstancePresentationParametersV1 : BaseParameters
    {
    }

    public sealed class InstancePresentationDetailsV1 : BaseResponse
    {
        public InstancePresentationDetailsV1(string name, long uiLocaleId, string? publicUrl, bool pairsDevices)
        {
            Name = name;
            UiLocaleId = uiLocaleId;
            PublicUrl = publicUrl;
            PairsDevices = pairsDevices;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.PublicUrl))]
        public string? PublicUrl { get; }

        // A TV without an account may ask for a pairing code.
        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool PairsDevices { get; }
    }
}
