using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
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

    // Where a profile stands with one title on its AniList list; the figures count episodes, or volumes for a book.
    public sealed class WorkAniListV1 : BaseResponse
    {
        public WorkAniListV1(bool isConnected, bool isOnList, string? status, bool isVolume, int progress, int? total, int localProgress, AniListSyncStateType? syncStateId)
        {
            IsConnected = isConnected;
            IsOnList = isOnList;
            Status = status;
            IsVolume = isVolume;
            Progress = progress;
            Total = total;
            LocalProgress = localProgress;
            SyncStateId = syncStateId;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsConnected))]
        public bool IsConnected { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsOnList))]
        public bool IsOnList { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.OptionalStatus))]
        public string? Status { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsManga))]
        public bool IsVolume { get; }

        // What AniList counts as done, and what the profile finished in a row here.
        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int Progress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? Total { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int LocalProgress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.SyncStateId))]
        public AniListSyncStateType? SyncStateId { get; }
    }

    // How many entries of the profile's list sit in one status.
    public sealed class AniListStatusCountV1 : BaseResponse
    {
        public AniListStatusCountV1(string status, int count)
        {
            Status = status;
            Count = count;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int Count { get; }
    }

    public sealed class AniListEntriesParametersV1 : BaseParameters
    {
        public AniListEntriesParametersV1(string status, int page = 1, int pageSize = 48)
        {
            Status = status;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // One entry of the profile's list, with the title of the library it belongs to, when there is one.
    public sealed class AniListEntryV1 : BaseResponse
    {
        public AniListEntryV1(int mediaId, long? workId, string title, string? coverUrl, bool isManga, string status, int progress, int? total)
        {
            MediaId = mediaId;
            WorkId = workId;
            Title = title;
            CoverUrl = coverUrl;
            IsManga = isManga;
            Status = status;
            Progress = progress;
            Total = total;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.OptionalWorkId))]
        public long? WorkId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Title))]
        public string Title { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.CoverUrl))]
        public string? CoverUrl { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsManga))]
        public bool IsManga { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }

        // Episodes or chapters done, and the total AniList knows.
        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int Progress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? Total { get; }
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
