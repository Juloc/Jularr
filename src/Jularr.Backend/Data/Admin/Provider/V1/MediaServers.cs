using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Admin.Provider.V1
{
    public sealed class MediaServerParametersV1 : BaseParameters
    {
        public MediaServerParametersV1(MediaServerType mediaServerTypeId)
        {
            MediaServerTypeId = mediaServerTypeId;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.MediaServerTypeId))]
        public MediaServerType MediaServerTypeId { get; }
    }

    public sealed class SaveMediaServerDataV1 : BaseData
    {
        public SaveMediaServerDataV1(string baseUrl, string? apiKey)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl), true)]
        public string BaseUrl { get; }

        // Write-only; empty keeps the stored key.
        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.Secret))]
        public string? ApiKey { get; }
    }

    public sealed class MediaServerDetailsV1 : BaseResponse
    {
        public MediaServerDetailsV1(bool isEnabled, string? baseUrl, bool hasSecret, string? externalId, DateTime? syncedAt)
        {
            IsEnabled = isEnabled;
            BaseUrl = baseUrl;
            HasSecret = hasSecret;
            ExternalId = externalId;
            SyncedAt = syncedAt;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl))]
        public string? BaseUrl { get; }

        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.HasSecret))]
        public bool HasSecret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.SyncedAt))]
        public DateTime? SyncedAt { get; }
    }

    public sealed class MediaServerTestV1 : BaseResponse
    {
        public MediaServerTestV1(string name, string? version, int userCount)
        {
            Name = name;
            Version = version;
            UserCount = userCount;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Name))]
        public string Name { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Version))]
        public string? Version { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Count))]
        public int UserCount { get; }
    }

    // A server user and the profile it shares progress with; unconfirmed profiles are proposals.
    public sealed class MediaServerLinkV1 : BaseResponse
    {
        public MediaServerLinkV1(long id, string externalName, long? profileId, string? profileName, bool isConfirmed, DateTime? syncedAt)
        {
            Id = id;
            ExternalName = externalName;
            ProfileId = profileId;
            ProfileName = profileName;
            IsConfirmed = isConfirmed;
            SyncedAt = syncedAt;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long Id { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ExternalName))]
        public string ExternalName { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId))]
        public long? ProfileId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileName))]
        public string? ProfileName { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.IsConfirmed))]
        public bool IsConfirmed { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.SyncedAt))]
        public DateTime? SyncedAt { get; }
    }

    public sealed class MediaServerLinkParametersV1 : BaseParameters
    {
        public MediaServerLinkParametersV1(MediaServerType mediaServerTypeId, long linkId)
        {
            MediaServerTypeId = mediaServerTypeId;
            LinkId = linkId;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.MediaServerTypeId))]
        public MediaServerType MediaServerTypeId { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.LinkId))]
        public long LinkId { get; }
    }

    // The administrator's decision: this profile, or none.
    public sealed class SaveMediaServerLinkDataV1 : BaseData
    {
        public SaveMediaServerLinkDataV1(long? profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.ProfileId))]
        public long? ProfileId { get; }
    }

    public sealed class MediaServerSyncResultV1 : BaseResponse
    {
        public MediaServerSyncResultV1(int localCount, int remoteCount)
        {
            LocalCount = localCount;
            RemoteCount = remoteCount;
        }

        // Positions taken over into Jularr and sent to the server.
        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Count))]
        public int LocalCount { get; }

        [DataField(typeof(MediaServerFields), nameof(MediaServerFields.Count))]
        public int RemoteCount { get; }
    }
}
