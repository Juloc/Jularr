using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.User.AniList.V1
{
    public sealed class AniListParametersV1 : BaseParameters
    {
    }

    // The profile's own AniList account; the token is never part of it.
    public sealed class AniListConnectionV1 : BaseResponse
    {
        public AniListConnectionV1(bool isConfigured, bool isConnected, string? viewerName, bool isPushEnabled, bool isExpired, DateTime? connectedAt, DateTime? syncedAt, string? failureCode,
            int entryCount, int matchedCount, string? authorizationUrl)
        {
            IsConfigured = isConfigured;
            IsConnected = isConnected;
            ViewerName = viewerName;
            IsPushEnabled = isPushEnabled;
            IsExpired = isExpired;
            ConnectedAt = connectedAt;
            SyncedAt = syncedAt;
            FailureCode = failureCode;
            EntryCount = entryCount;
            MatchedCount = matchedCount;
            AuthorizationUrl = authorizationUrl;
        }

        // The Owner entered the application's client id and secret, and AniList is switched on.
        [DataField(typeof(AniListFields), nameof(AniListFields.IsConfigured))]
        public bool IsConfigured { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsConnected))]
        public bool IsConnected { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.ViewerName))]
        public string? ViewerName { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsPushEnabled))]
        public bool IsPushEnabled { get; }

        // The token has run out; the profile connects again.
        [DataField(typeof(AniListFields), nameof(AniListFields.IsExpired))]
        public bool IsExpired { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? ConnectedAt { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? SyncedAt { get; }

        // Why the last pass did not finish, as an error code.
        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode))]
        public string? FailureCode { get; }

        // Entries of the list as last read, and those that belong to a title of the library.
        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int EntryCount { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int MatchedCount { get; }

        // Where the person authorizes Jularr; the PIN page then shows the code to paste. Null until the Owner configured it.
        [DataField(typeof(AniListFields), nameof(AniListFields.AuthorizationUrl))]
        public string? AuthorizationUrl { get; }
    }

    // The code AniList's PIN page showed.
    public sealed class ConnectAniListDataV1 : BaseData
    {
        public ConnectAniListDataV1(string code)
        {
            Code = code;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.Code))]
        public string Code { get; }
    }

    public sealed class UpdateAniListPushDataV1 : BaseData
    {
        public UpdateAniListPushDataV1(bool isPushEnabled)
        {
            IsPushEnabled = isPushEnabled;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsPushEnabled))]
        public bool IsPushEnabled { get; }
    }
}
