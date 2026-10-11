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

    public sealed class ReadProfileResultV1 : BaseResponse
    {
        public ReadProfileResultV1(long id, string displayName, long uiLocaleId)
        {
            Id = id;
            DisplayName = displayName;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }
    }
}
