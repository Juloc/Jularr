using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.Fields.Profile
{
    public static class ProfileSpeechSettingFields
    {
        public static readonly LongField ProfileId = ProfileFields.Id;
        public static readonly DecimalField Rate = SpeechFields.Rate;
        public static readonly DecimalField Pitch = SpeechFields.Pitch;
        public static readonly DecimalField Volume = SpeechFields.Volume;
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
