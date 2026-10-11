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
        public ProfileSelectionDetailsV1(long id, string displayName, bool isLocked)
        {
            Id = id;
            DisplayName = displayName;
            IsLocked = isLocked;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        public bool IsLocked { get; }
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

        [System.Text.Json.Serialization.JsonIgnore]
        public SessionCredentialsData? Session { get; }
    }
}
