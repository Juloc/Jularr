using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Admin.Provider.V1
{
    public sealed class PlexServerParametersV1 : BaseParameters
    {
    }

    public sealed class SavePlexServerDataV1 : BaseData
    {
        public SavePlexServerDataV1(string baseUrl, string? token)
        {
            BaseUrl = baseUrl;
            Token = token;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl), true)]
        public string BaseUrl { get; }

        // Write-only; empty keeps the stored token.
        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.Secret))]
        public string? Token { get; }
    }

    public sealed class PlexServerDetailsV1 : BaseResponse
    {
        public PlexServerDetailsV1(bool isEnabled, string? baseUrl, bool hasSecret, string? externalId, DateTime? syncedAt, int linkedCount)
        {
            IsEnabled = isEnabled;
            BaseUrl = baseUrl;
            HasSecret = hasSecret;
            ExternalId = externalId;
            SyncedAt = syncedAt;
            LinkedCount = linkedCount;
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

        [DataField(typeof(PlexFields), nameof(PlexFields.LinkedCount))]
        public int LinkedCount { get; }
    }

    public sealed class PlexServerTestV1 : BaseResponse
    {
        public PlexServerTestV1(string externalId, string? version, string[] sections)
        {
            ExternalId = externalId;
            Version = version;
            Sections = sections;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string ExternalId { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.Version))]
        public string? Version { get; }

        // Movie and show libraries by title.
        [DataField(typeof(PlexFields), nameof(PlexFields.Sections))]
        public string[] Sections { get; }
    }

    public sealed class PlexSyncResultV1 : BaseResponse
    {
        public PlexSyncResultV1(int linkedCount, int createdCount)
        {
            LinkedCount = linkedCount;
            CreatedCount = createdCount;
        }

        [DataField(typeof(PlexFields), nameof(PlexFields.LinkedCount))]
        public int LinkedCount { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.CreatedCount))]
        public int CreatedCount { get; }
    }
}
