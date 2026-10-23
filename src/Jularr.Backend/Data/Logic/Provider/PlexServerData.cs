using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Provider
{
    internal sealed class PlexServerStateData : BaseData
    {
        public PlexServerStateData(bool isEnabled, string? baseUrl, byte[]? protectedSecret, string? externalId)
        {
            IsEnabled = isEnabled;
            BaseUrl = baseUrl;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

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

    internal sealed class PlexServerSaveData : BaseData
    {
        public PlexServerSaveData(string? baseUrl, byte[]? protectedSecret, string? externalId)
        {
            BaseUrl = baseUrl;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl))]
        public string? BaseUrl { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }
    }

    internal sealed class PlexItemsData : BaseData
    {
        public PlexItemsData(string items)
        {
            Items = items;
        }

        // JSON rows of Key (the rating key), MediaTypeId, Title, Year, TmdbId, ImdbId and TvdbId.
        [DataField(typeof(PlexFields), nameof(PlexFields.Items))]
        public string Items { get; }
    }

    internal sealed class PlexKeysData : BaseData
    {
        public PlexKeysData(string[] ratingKeys)
        {
            RatingKeys = ratingKeys;
        }

        [DataField(typeof(PlexFields), nameof(PlexFields.RatingKeys))]
        public string[] RatingKeys { get; }
    }

    internal sealed class PlexItemsResultData : BaseData
    {
        public PlexItemsResultData(int linkedCount, int createdCount)
        {
            LinkedCount = linkedCount;
            CreatedCount = createdCount;
        }

        [DataField(typeof(PlexFields), nameof(PlexFields.LinkedCount))]
        public int LinkedCount { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.CreatedCount))]
        public int CreatedCount { get; }
    }

    // Plex sign-in as the Owner set it, on the connected server; new accounts start in the instance's language.
    internal sealed class PlexSignInData : BaseData
    {
        public PlexSignInData(bool signIn, bool registration, bool registrationApproval, string[] registrationSections, long uiLocaleId, string? baseUrl, string? serverIdentifier)
        {
            SignIn = signIn;
            Registration = registration;
            RegistrationApproval = registrationApproval;
            RegistrationSections = registrationSections;
            UiLocaleId = uiLocaleId;
            BaseUrl = baseUrl;
            ServerIdentifier = serverIdentifier;
        }

        [DataField(typeof(PlexFields), nameof(PlexFields.SignIn))]
        public bool SignIn { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.Registration))]
        public bool Registration { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.RegistrationApproval))]
        public bool RegistrationApproval { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.SectionKeys))]
        public string[] RegistrationSections { get; }

        [DataField(typeof(Fields.Instance.InstanceSettingsFields), nameof(Fields.Instance.InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.BaseUrl))]
        public string? BaseUrl { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ServerIdentifier { get; }

        // Registration needs a connected server to ask about libraries.
        public bool CanRegister => SignIn && Registration && BaseUrl is not null && ServerIdentifier is not null;
    }

    internal sealed class PlexSignInSaveData : BaseData
    {
        public PlexSignInSaveData(bool signIn, bool registration, bool registrationApproval, string[] registrationSections)
        {
            SignIn = signIn;
            Registration = registration;
            RegistrationApproval = registrationApproval;
            RegistrationSections = registrationSections;
        }

        [DataField(typeof(PlexFields), nameof(PlexFields.SignIn))]
        public bool SignIn { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.Registration))]
        public bool Registration { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.RegistrationApproval))]
        public bool RegistrationApproval { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.SectionKeys))]
        public string[] RegistrationSections { get; }
    }
}
