using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.Public.Authentication.V1;

namespace Jularr.Backend.Data.User.Profile.V1
{
    public sealed class ReadProfilesParametersV1 : BaseParameters
    {
        public ReadProfilesParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class SelectProfileParametersV1 : BaseParameters
    {
        public SelectProfileParametersV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    public sealed class ProfileSelectionDetailsV1 : BaseResponse
    {
        public ProfileSelectionDetailsV1(long id, string displayName, bool isLocked, int? pinLength, bool isOwned)
        {
            Id = id;
            DisplayName = displayName;
            IsLocked = isLocked;
            PinLength = pinLength;
            IsOwned = isOwned;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        public bool IsLocked { get; }

        // A PIN confirms itself once this many digits are entered.
        [DataField(typeof(ProfileFields), nameof(ProfileFields.PinLength))]
        public int? PinLength { get; }

        // Owned profiles count against the account's limit.
        public bool IsOwned { get; }
    }

    public sealed class CreateProfileDataV1 : BaseData
    {
        public CreateProfileDataV1(string displayName, long uiLocaleId)
        {
            DisplayName = displayName;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }
    }

    public sealed class VerifyProfilePinDataV1 : BaseData
    {
        public VerifyProfilePinDataV1(string pin)
        {
            Pin = pin;
        }

        [DataField(typeof(ProfilePinFields), nameof(ProfilePinFields.Pin))]
        public string Pin { get; }
    }

    public sealed class SelectProfileResultV1 : BaseResponse, ISessionResult
    {
        internal SelectProfileResultV1(SessionCredentialsData session)
        {
            ProfileId = session.ProfileId!.Value;
            Session = session;
        }

        public SelectProfileResultV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [JsonIgnore]
        public SessionCredentialsData? Session { get; }
    }
}
