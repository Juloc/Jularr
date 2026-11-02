using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.AniList
{
    internal sealed class AniListProfileData : BaseData
    {
        public AniListProfileData(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }
    }

    // The application the Owner registered at AniList: its public id and protected secret.
    internal sealed class AniListApplicationData : BaseData
    {
        public AniListApplicationData(bool isEnabled, string? clientId, byte[]? protectedSecret)
        {
            IsEnabled = isEnabled;
            ClientId = clientId;
            ProtectedSecret = protectedSecret;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ClientId { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }
    }

    // What the profile page shows of the account, with the Owner's client id the authorization address needs.
    internal sealed class AniListConnectionRowData : BaseData
    {
        public AniListConnectionRowData(bool isConfigured, string? clientId, bool isConnected, string? viewerName, bool isPushEnabled, bool isExpired, DateTime? connectedAt, DateTime? syncedAt,
            string? failureCode, int entryCount, int matchedCount)
        {
            IsConfigured = isConfigured;
            ClientId = clientId;
            IsConnected = isConnected;
            ViewerName = viewerName;
            IsPushEnabled = isPushEnabled;
            IsExpired = isExpired;
            ConnectedAt = connectedAt;
            SyncedAt = syncedAt;
            FailureCode = failureCode;
            EntryCount = entryCount;
            MatchedCount = matchedCount;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsConfigured))]
        public bool IsConfigured { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ClientId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsConnected))]
        public bool IsConnected { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.ViewerName))]
        public string? ViewerName { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsPushEnabled))]
        public bool IsPushEnabled { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsExpired))]
        public bool IsExpired { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? ConnectedAt { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? SyncedAt { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode))]
        public string? FailureCode { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int EntryCount { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int MatchedCount { get; }
    }

    internal sealed class AniListConnectionSaveData : BaseData
    {
        public AniListConnectionSaveData(long profileId, int viewerId, string viewerName, byte[] protectedToken, DateTime? expiresAt)
        {
            ProfileId = profileId;
            ViewerId = viewerId;
            ViewerName = viewerName;
            ProtectedToken = protectedToken;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.ViewerId))]
        public int ViewerId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.RequiredViewerName))]
        public string ViewerName { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret), true)]
        public byte[] ProtectedToken { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? ExpiresAt { get; }
    }

    internal sealed class AniListPushSaveData : BaseData
    {
        public AniListPushSaveData(long profileId, bool isPushEnabled)
        {
            ProfileId = profileId;
            IsPushEnabled = isPushEnabled;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsPushEnabled))]
        public bool IsPushEnabled { get; }
    }

    // A connection a pass works on.
    internal sealed class AniListSyncConnectionData : BaseData
    {
        public AniListSyncConnectionData(long profileId, int viewerId, byte[] protectedToken, DateTime? expiresAt, DateTime? pushEnabledAt)
        {
            ProfileId = profileId;
            ViewerId = viewerId;
            ProtectedToken = protectedToken;
            ExpiresAt = expiresAt;
            PushEnabledAt = pushEnabledAt;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.ViewerId))]
        public int ViewerId { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret), true)]
        public byte[] ProtectedToken { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? ExpiresAt { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? PushEnabledAt { get; }
    }

    internal sealed class AniListEntriesSaveData : BaseData
    {
        public AniListEntriesSaveData(long profileId, string entries)
        {
            ProfileId = profileId;
            Entries = entries;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        // JSON rows of MediaId, ListEntryId, IsManga, Title, Status, Progress, ProgressVolumes, Score, TotalCount, TotalVolumes and UpdatedAt.
        [DataField(typeof(AniListFields), nameof(AniListFields.Rows))]
        public string Entries { get; }
    }

    // An entry of the list with what the rules need to decide.
    internal sealed class AniListEntryData : BaseData
    {
        public AniListEntryData(int mediaId, long listEntryId, bool isManga, string status, int progress, int progressVolumes, int? totalCount, int? totalVolumes, int attemptCount = 0)
        {
            AttemptCount = attemptCount;
            MediaId = mediaId;
            ListEntryId = listEntryId;
            IsManga = isManga;
            Status = status;
            Progress = progress;
            ProgressVolumes = progressVolumes;
            TotalCount = totalCount;
            TotalVolumes = totalVolumes;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.ListEntryId))]
        public long ListEntryId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsManga))]
        public bool IsManga { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int Progress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int ProgressVolumes { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? TotalCount { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? TotalVolumes { get; }

        // Writes that failed in a row.
        [DataField(typeof(AniListFields), nameof(AniListFields.Count))]
        public int AttemptCount { get; }
    }

    // The entries a pass looked at for sending, and when.
    internal sealed class AniListCheckedData : BaseData
    {
        public AniListCheckedData(long profileId, string mediaIds, DateTime checkedAt)
        {
            ProfileId = profileId;
            MediaIds = mediaIds;
            CheckedAt = checkedAt;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        // A JSON array of AniList ids.
        [DataField(typeof(AniListFields), nameof(AniListFields.Rows))]
        public string MediaIds { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.RequiredMoment))]
        public DateTime CheckedAt { get; }
    }

    internal sealed class AniListUnitsReadData : BaseData
    {
        public AniListUnitsReadData(long profileId, string mediaIds)
        {
            ProfileId = profileId;
            MediaIds = mediaIds;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        // A JSON array of AniList ids.
        [DataField(typeof(AniListFields), nameof(AniListFields.Rows))]
        public string MediaIds { get; }
    }

    // One film, episode or volume an entry covers, as the profile has it.
    internal sealed class AniListUnitData : BaseData
    {
        public AniListUnitData(int mediaId, long workId, long? workUnitId, int number, bool isCompleted, DateTime? completedAt, bool isVolume)
        {
            MediaId = mediaId;
            WorkId = workId;
            WorkUnitId = workUnitId;
            Number = number;
            IsCompleted = isCompleted;
            CompletedAt = completedAt;
            IsVolume = isVolume;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.WorkUnitId))]
        public long? WorkUnitId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Number))]
        public int Number { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsCompleted))]
        public bool IsCompleted { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Moment))]
        public DateTime? CompletedAt { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsManga))]
        public bool IsVolume { get; }
    }

    internal sealed class AniListImportData : BaseData
    {
        public AniListImportData(long profileId, string completions, string mediaIds)
        {
            ProfileId = profileId;
            Completions = completions;
            MediaIds = mediaIds;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        // JSON rows of WorkId, WorkUnitId and IsVolume to complete.
        [DataField(typeof(AniListFields), nameof(AniListFields.Rows))]
        public string Completions { get; }

        // A JSON array of the entries that were looked at; the matched ones are taken over.
        [DataField(typeof(AniListFields), nameof(AniListFields.Rows))]
        public string MediaIds { get; }
    }

    // A progress AniList confirmed.
    internal sealed class AniListPushedData : BaseData
    {
        public AniListPushedData(long profileId, int mediaId, int progress, int progressVolumes, DateTime updatedAt)
        {
            ProfileId = profileId;
            MediaId = mediaId;
            Progress = progress;
            ProgressVolumes = progressVolumes;
            UpdatedAt = updatedAt;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int Progress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int ProgressVolumes { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.RequiredMoment))]
        public DateTime UpdatedAt { get; }
    }

    // A write that failed: tried again after a growing wait.
    internal sealed class AniListFailedData : BaseData
    {
        public AniListFailedData(long profileId, int mediaId, string errorCode, DateTime nextAttemptAt)
        {
            ProfileId = profileId;
            MediaId = mediaId;
            ErrorCode = errorCode;
            NextAttemptAt = nextAttemptAt;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode), true)]
        public string ErrorCode { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.RequiredMoment))]
        public DateTime NextAttemptAt { get; }
    }

    internal sealed class AniListPassData : BaseData
    {
        public AniListPassData(long profileId, string? failureCode)
        {
            ProfileId = profileId;
            FailureCode = failureCode;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode))]
        public string? FailureCode { get; }
    }

    // The entry of the profile's list that stands for a title of the library.
    internal sealed class WorkAniListEntryData : BaseData
    {
        public WorkAniListEntryData(int mediaId, bool isManga, string status, int progress, int progressVolumes, int? totalCount, int? totalVolumes, string? lastError, bool isPushEnabled, bool isExpired,
            string? failureCode)
        {
            LastError = lastError;
            IsPushEnabled = isPushEnabled;
            IsExpired = isExpired;
            FailureCode = failureCode;
            MediaId = mediaId;
            IsManga = isManga;
            Status = status;
            Progress = progress;
            ProgressVolumes = progressVolumes;
            TotalCount = totalCount;
            TotalVolumes = totalVolumes;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsManga))]
        public bool IsManga { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int Progress { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Progress))]
        public int ProgressVolumes { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? TotalCount { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.Total))]
        public int? TotalVolumes { get; }

        // The code of the last refused write of the entry, and of the last failed pass of the account.
        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode))]
        public string? LastError { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsPushEnabled))]
        public bool IsPushEnabled { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsExpired))]
        public bool IsExpired { get; }

        [DataField(typeof(AniListFields), nameof(AniListFields.FailureCode))]
        public string? FailureCode { get; }
    }

    internal sealed class AniListStatusData : BaseData
    {
        public AniListStatusData(string status)
        {
            Status = status;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.Status))]
        public string Status { get; }
    }

    internal sealed class AniListMediaData : BaseData
    {
        public AniListMediaData(int mediaId)
        {
            MediaId = mediaId;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.MediaId))]
        public int MediaId { get; }
    }

    internal sealed class AniListFlagData : BaseData
    {
        public AniListFlagData(bool isConnected)
        {
            IsConnected = isConnected;
        }

        [DataField(typeof(AniListFields), nameof(AniListFields.IsConnected))]
        public bool IsConnected { get; }
    }
}
