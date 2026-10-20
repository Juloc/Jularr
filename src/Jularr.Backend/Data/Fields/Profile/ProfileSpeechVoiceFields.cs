using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.Fields.Profile
{
    public static class ProfileSpeechVoiceFields
    {
        public static readonly LongField ProfileId = ProfileFields.Id;
        public static readonly StringField Language = SpeechFields.Language;
        public static readonly EnumField ProviderTypeId = SpeechFields.ProviderTypeId;
        public static readonly StringField ModelId = SpeechFields.ModelId;
        public static readonly StringField VoiceId = SpeechFields.VoiceId;
    }
}
