using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.Logic.Speech
{
    internal sealed class SpeechSettingsData(decimal rate, decimal pitch, decimal volume, long version) : BaseData
    {
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Rate))]
        public decimal Rate { get; } = rate;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Pitch))]
        public decimal Pitch { get; } = pitch;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Volume))]
        public decimal Volume { get; } = volume;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; } = version;
    }

    internal sealed class SpeechPreferencesData : BaseData
    {
        public SpeechPreferencesData(long profileId, decimal rate, decimal pitch, decimal volume, long version, string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            ProfileId = profileId;
            Rate = rate;
            Pitch = pitch;
            Volume = volume;
            Version = version;
            Language = language;
            ProviderTypeId = providerTypeId;
            ModelId = modelId;
            VoiceId = voiceId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
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
