using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.Admin.Speech.V1
{
    public sealed class UpdateSpeechModelDataV1 : BaseData
    {
        public UpdateSpeechModelDataV1(bool isEnabled, long version)
        {
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.IsEnabled))]
        public bool IsEnabled { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; }
    }
}
