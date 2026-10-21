using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Playback.V1
{
    public sealed class PlaybackPreferencesParametersV1 : BaseParameters
    {
    }

    // The profile's playback choices and how the asking device plays.
    public sealed class PlaybackPreferencesV1 : BaseResponse
    {
        public PlaybackPreferencesV1(string? audioLanguage, string? subtitleLanguage, SubtitleMode subtitleModeTypeId, decimal speed, bool autoplayNext, SkipMode introSkipModeTypeId,
            SkipMode outroSkipModeTypeId, string profileLanguage, PlaybackQuality deviceQuality)
        {
            AudioLanguage = audioLanguage;
            SubtitleLanguage = subtitleLanguage;
            SubtitleModeTypeId = subtitleModeTypeId;
            Speed = speed;
            AutoplayNext = autoplayNext;
            IntroSkipModeTypeId = introSkipModeTypeId;
            OutroSkipModeTypeId = outroSkipModeTypeId;
            ProfileLanguage = profileLanguage;
            DeviceQuality = deviceQuality;
        }

        // No audio language keeps the file's default track.
        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? AudioLanguage { get; }

        // No subtitle language follows the profile's language.
        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language))]
        public string? SubtitleLanguage { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SubtitleModeTypeId))]
        public SubtitleMode SubtitleModeTypeId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Speed))]
        public decimal Speed { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.AutoplayNext))]
        public bool AutoplayNext { get; }

        // What happens at an intro or recap, and at credits or a preview.
        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SkipModeTypeId))]
        public SkipMode IntroSkipModeTypeId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SkipModeTypeId))]
        public SkipMode OutroSkipModeTypeId { get; }

        // The language of the profile's interface, the subtitle language it has when none is chosen.
        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Language), true)]
        public string ProfileLanguage { get; }

        // This device's own choice; it is not part of the profile.
        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PlaybackQuality))]
        public PlaybackQuality DeviceQuality { get; }
    }

    public sealed class SavePlaybackPreferencesDataV1 : BaseData
    {
        public SavePlaybackPreferencesDataV1(string? audioLanguage, string? subtitleLanguage, SubtitleMode subtitleModeTypeId, decimal speed, bool autoplayNext, SkipMode introSkipModeTypeId,
            SkipMode outroSkipModeTypeId)
        {
            AudioLanguage = audioLanguage;
            SubtitleLanguage = subtitleLanguage;
            SubtitleModeTypeId = subtitleModeTypeId;
            Speed = speed;
            AutoplayNext = autoplayNext;
            IntroSkipModeTypeId = introSkipModeTypeId;
            OutroSkipModeTypeId = outroSkipModeTypeId;
        }

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

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SkipModeTypeId))]
        public SkipMode IntroSkipModeTypeId { get; }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.SkipModeTypeId))]
        public SkipMode OutroSkipModeTypeId { get; }
    }

    // The speed chosen in the player, remembered for the profile.
    public sealed class SavePlaybackSpeedDataV1 : BaseData
    {
        public SavePlaybackSpeedDataV1(decimal speed)
        {
            Speed = speed;
        }

        [DataField(typeof(PlaybackPreferenceFields), nameof(PlaybackPreferenceFields.Speed))]
        public decimal Speed { get; }
    }
}
