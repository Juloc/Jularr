using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class PlaybackPreferenceData : BaseData
    {
        public PlaybackPreferenceData(long profileId, string? audioLanguage, string? subtitleLanguage, SubtitleMode subtitleModeTypeId, decimal speed, bool autoplayNext)
        {
            ProfileId = profileId;
            AudioLanguage = audioLanguage;
            SubtitleLanguage = subtitleLanguage;
            SubtitleModeTypeId = subtitleModeTypeId;
            Speed = speed;
            AutoplayNext = autoplayNext;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? AudioLanguage { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? SubtitleLanguage { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SubtitleModeTypeId))]
        public SubtitleMode SubtitleModeTypeId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Speed))]
        public decimal Speed { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.AutoplayNext))]
        public bool AutoplayNext { get; }
    }

    // The profile and the device asking.
    internal sealed class PlaybackPreferenceLookupData : BaseData
    {
        public PlaybackPreferenceLookupData(long profileId, Guid? sessionPublicId)
        {
            ProfileId = profileId;
            SessionPublicId = sessionPublicId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.PublicId), false)]
        public Guid? SessionPublicId { get; }
    }

    internal sealed class PlaybackSpeedData : BaseData
    {
        public PlaybackSpeedData(long profileId, decimal speed)
        {
            ProfileId = profileId;
            Speed = speed;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Speed))]
        public decimal Speed { get; }
    }

    // The preferences and language the track choice follows; a profile without a row has the defaults.
    internal sealed class PlaybackChoiceData : BaseData
    {
        public PlaybackChoiceData(string? audioLanguage, string? subtitleLanguage, SubtitleMode subtitleModeTypeId, string profileLanguage)
        {
            AudioLanguage = audioLanguage;
            SubtitleLanguage = subtitleLanguage;
            SubtitleModeTypeId = subtitleModeTypeId;
            ProfileLanguage = profileLanguage;
        }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? AudioLanguage { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? SubtitleLanguage { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SubtitleModeTypeId))]
        public SubtitleMode SubtitleModeTypeId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language), true)]
        public string ProfileLanguage { get; }
    }
}
