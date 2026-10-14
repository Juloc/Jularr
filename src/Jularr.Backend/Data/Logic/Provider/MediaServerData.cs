using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Provider
{
    internal sealed class MediaServerStateData : BaseData
    {
        public MediaServerStateData(long id, bool isEnabled, string? baseUrl, byte[]? protectedSecret, string? externalId)
        {
            Id = id;
            IsEnabled = isEnabled;
            BaseUrl = baseUrl;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl))]
        public string? BaseUrl { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }
    }

    internal sealed class MediaServerSaveData : BaseData
    {
        public MediaServerSaveData(string providerKey, string? baseUrl, byte[]? protectedSecret, string? externalId)
        {
            ProviderKey = providerKey;
            BaseUrl = baseUrl;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string ProviderKey { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl))]
        public string? BaseUrl { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }
    }

    internal sealed class MediaServerUsersData : BaseData
    {
        public MediaServerUsersData(string providerKey, string users)
        {
            ProviderKey = providerKey;
            Users = users;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string ProviderKey { get; }

        // JSON rows of Id and Name.
        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Rows))]
        public string Users { get; }
    }

    internal sealed class MediaServerLinkSaveData : BaseData
    {
        public MediaServerLinkSaveData(string providerKey, long linkId, long? profileId)
        {
            ProviderKey = providerKey;
            LinkId = linkId;
            ProfileId = profileId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string ProviderKey { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long LinkId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId))]
        public long? ProfileId { get; }
    }

    // A confirmed user whose progress is shared.
    internal sealed class MediaServerSyncLinkData : BaseData
    {
        public MediaServerSyncLinkData(long id, string externalUserId, long profileId)
        {
            Id = id;
            ExternalUserId = externalUserId;
            ProfileId = profileId;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long Id { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ExternalUserId))]
        public string ExternalUserId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId), true)]
        public long ProfileId { get; }
    }

    internal sealed class MediaServerItemsData : BaseData
    {
        public MediaServerItemsData(long providerProfileLinkId, long profileId, string items)
        {
            ProviderProfileLinkId = providerProfileLinkId;
            ProfileId = profileId;
            Items = items;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long ProviderProfileLinkId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId), true)]
        public long ProfileId { get; }

        // JSON rows of Id, MediaTypeId, TmdbNamespace, TmdbId, ImdbId, TvdbId, Season, Episode, PositionMs, IsPlayed and DurationMs.
        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Rows))]
        public string Items { get; }
    }

    // One server item matched to a Work or episode, with both sides and the state last agreed.
    internal sealed class ProgressCandidateData : BaseData
    {
        public ProgressCandidateData(string externalItemId, long remotePositionMs, bool remoteIsCompleted, long? durationMs, long workId, long? workUnitId,
            long? localPositionMs, bool? localIsCompleted, DateTime? localUpdatedAt, long? syncedPositionMs, bool? syncedIsCompleted, DateTime? syncedAt)
        {
            ExternalItemId = externalItemId;
            RemotePositionMs = remotePositionMs;
            RemoteIsCompleted = remoteIsCompleted;
            DurationMs = durationMs;
            WorkId = workId;
            WorkUnitId = workUnitId;
            LocalPositionMs = localPositionMs;
            LocalIsCompleted = localIsCompleted;
            LocalUpdatedAt = localUpdatedAt;
            SyncedPositionMs = syncedPositionMs;
            SyncedIsCompleted = syncedIsCompleted;
            SyncedAt = syncedAt;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ExternalItemId))]
        public string ExternalItemId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long RemotePositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool RemoteIsCompleted { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? WorkUnitId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? LocalPositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted), false)]
        public bool? LocalIsCompleted { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ChangedAt))]
        public DateTime? LocalUpdatedAt { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? SyncedPositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted), false)]
        public bool? SyncedIsCompleted { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ChangedAt))]
        public DateTime? SyncedAt { get; }
    }

    internal sealed class SyncedProgressData : BaseData
    {
        public SyncedProgressData(long providerProfileLinkId, long profileId, string local, string states)
        {
            ProviderProfileLinkId = providerProfileLinkId;
            ProfileId = profileId;
            Local = local;
            States = states;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long ProviderProfileLinkId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId), true)]
        public long ProfileId { get; }

        // JSON rows of WorkId, WorkUnitId, PositionMs, DurationMs and IsCompleted to take over.
        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Rows))]
        public string Local { get; }

        // JSON rows of ExternalItemId, PositionMs and IsCompleted now agreed.
        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Rows))]
        public string States { get; }
    }

    internal sealed class MediaServerLinkIdData : BaseData
    {
        public MediaServerLinkIdData(long providerProfileLinkId)
        {
            ProviderProfileLinkId = providerProfileLinkId;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long ProviderProfileLinkId { get; }
    }
}
