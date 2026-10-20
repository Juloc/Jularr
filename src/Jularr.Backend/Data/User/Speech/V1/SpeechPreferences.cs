using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.User.Speech.V1
{
    public sealed class SpeechPreferencesV1 : BaseResponse
    {
        public SpeechPreferencesV1(decimal rate, decimal pitch, decimal volume, long version, BaseDataList<SpeechVoiceV1> voices)
        {
            Rate = rate;
            Pitch = pitch;
            Volume = volume;
            Version = version;
            Voices = voices;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.Rate))]
        public decimal Rate { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Pitch))]
        public decimal Pitch { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Volume))]
        public decimal Volume { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; }
        public BaseDataList<SpeechVoiceV1> Voices { get; }
    }

    public sealed class SpeechVoiceV1 : BaseResponse
    {
        public SpeechVoiceV1(string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            Language = language;
            ProviderTypeId = providerTypeId;
            ModelId = modelId;
            VoiceId = voiceId;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.Language))]
        public string Language { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.ProviderTypeId))]
        public SpeechProviderType ProviderTypeId { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.ModelId))]
        public string? ModelId { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.VoiceId))]
        public string? VoiceId { get; }
    }

    public sealed class SaveSpeechPreferencesDataV1 : BaseData
    {
        public SaveSpeechPreferencesDataV1(decimal rate, decimal pitch, decimal volume, long version, string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            Rate = rate;
            Pitch = pitch;
            Volume = volume;
            Version = version;
            Language = language;
            ProviderTypeId = providerTypeId;
            ModelId = modelId;
            VoiceId = voiceId;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.Rate))]
        public decimal Rate { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Pitch))]
        public decimal Pitch { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Volume))]
        public decimal Volume { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Language))]
        public string Language { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.ProviderTypeId))]
        public SpeechProviderType ProviderTypeId { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.ModelId))]
        public string? ModelId { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.VoiceId))]
        public string? VoiceId { get; }
    }
}
