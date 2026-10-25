using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.User.Profile.V1
{
    public sealed class ReadProfileParametersV1 : BaseParameters
    {
        public ReadProfileParametersV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    public sealed class UpdateProfileParametersV1 : BaseParameters
    {
        public UpdateProfileParametersV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    [JsonConverter(typeof(PartialDataJsonConverter<UpdateProfileDataV1>))]
    public sealed class UpdateProfileDataV1 : BaseData
    {
        private string? _displayName;
        private long? _uiLocaleId;

        public UpdateProfileDataV1() : base(trackChanges: true)
        {
        }

        public UpdateProfileDataV1(string displayName) : this()
        {
            DisplayName = displayName;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName), false)]
        public string? DisplayName
        {
            get => _displayName;
            init => SetField(ref _displayName, value);
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId), false)]
        public long? UiLocaleId
        {
            get => _uiLocaleId;
            init => SetField(ref _uiLocaleId, value);
        }
    }

    public sealed class UpdateProfilePinDataV1 : BaseData
    {
        public UpdateProfilePinDataV1(string pin)
        {
            Pin = pin;
        }

        [DataField(typeof(ProfilePinFields), nameof(ProfilePinFields.Pin))]
        public string Pin { get; }
    }

    public sealed class ReadProfileResultV1 : BaseResponse
    {
        public ReadProfileResultV1(long id, string displayName, long uiLocaleId, bool hasPin, bool autoplayPreviews = true, string accentHex = "#8040f7")
        {
            AutoplayPreviews = autoplayPreviews;
            AccentHex = accentHex;
            Id = id;
            DisplayName = displayName;
            UiLocaleId = uiLocaleId;
            HasPin = hasPin;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }

        public bool HasPin { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.AccentHex))]
        public string AccentHex { get; }

        // Whether a card that lingers on a touch device plays its preview by itself; the profile's playback preference.
        public bool AutoplayPreviews { get; }
    }
}
