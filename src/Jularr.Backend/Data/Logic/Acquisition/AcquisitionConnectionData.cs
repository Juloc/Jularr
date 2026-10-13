using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionConnectionData : BaseData
    {
        public AcquisitionConnectionData(long id, string name, AcquisitionConnectionType acquisitionConnectionTypeId, string baseUrl, byte[]? protectedApiKey, bool isEnabled, int priority,
            int[] categories, string? downloadCategory, string? remotePath, string? localPath, string? capabilities, long version, string? username,
            int? maxConnections)
        {
            Username = username;
            MaxConnections = maxConnections;
            Id = id;
            Name = name;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
            ProtectedApiKey = protectedApiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Categories = categories;
            DownloadCategory = downloadCategory;
            RemotePath = remotePath;
            LocalPath = localPath;
            Capabilities = capabilities;
            Version = version;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }

        [JsonIgnore]
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ProtectedApiKey), false)]
        public byte[]? ProtectedApiKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Priority))]
        public int Priority { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Categories))]
        public int[] Categories { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? DownloadCategory { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.RemotePath))]
        public string? RemotePath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.LocalPath))]
        public string? LocalPath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Capabilities))]
        public string? Capabilities { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }

        // OPDS catalogs and news servers may need an account; the password is the protected key.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Username))]
        public string? Username { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MaxConnections))]
        public int? MaxConnections { get; }
    }

    internal sealed class AcquisitionConnectionSaveData : BaseData
    {
        public AcquisitionConnectionSaveData(long? id, string name, AcquisitionConnectionType acquisitionConnectionTypeId, string baseUrl, byte[]? protectedApiKey, bool isEnabled, int priority,
            int[] categories, string? downloadCategory, string? remotePath, string? localPath, string? username,
            int? maxConnections)
        {
            Username = username;
            MaxConnections = maxConnections;
            Id = id;
            Name = name;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
            ProtectedApiKey = protectedApiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Categories = categories;
            DownloadCategory = downloadCategory;
            RemotePath = remotePath;
            LocalPath = localPath;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }

        [JsonIgnore]
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ProtectedApiKey), false)]
        public byte[]? ProtectedApiKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Priority))]
        public int Priority { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Categories))]
        public int[] Categories { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? DownloadCategory { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.RemotePath))]
        public string? RemotePath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.LocalPath))]
        public string? LocalPath { get; }

        // OPDS catalogs and news servers may need an account; the password is the protected key.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Username))]
        public string? Username { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MaxConnections))]
        public int? MaxConnections { get; }
    }

    internal sealed class AcquisitionConnectionCheckData : BaseData
    {
        public AcquisitionConnectionCheckData(long id, long version, string? capabilities, string? errorKey)
        {
            Id = id;
            Version = version;
            Capabilities = capabilities;
            ErrorKey = errorKey;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Capabilities))]
        public string? Capabilities { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }
    }

    internal sealed class AcquisitionConnectionParameters : BaseData
    {
        public AcquisitionConnectionParameters(long acquisitionConnectionId, long? version = null)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            Version = version;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class AcquisitionConnectionVersionData : BaseData
    {
        public AcquisitionConnectionVersionData(long version)
        {
            Version = version;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }
    }

    internal sealed class AcquisitionConnectionIdData : BaseData
    {
        public AcquisitionConnectionIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }
    }

    internal sealed class SourceConnectionData : BaseData
    {
        public SourceConnectionData(AcquisitionConnectionType acquisitionConnectionTypeId, string baseUrl)
        {
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }
    }

    internal sealed class SourceProviderData : BaseData
    {
        public SourceProviderData(string key, bool isEnabled)
        {
            Key = key;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(Fields.Provider.ProviderFields), nameof(Fields.Provider.ProviderFields.Key))]
        public string Key { get; }

        [DataField(typeof(Fields.Provider.ProviderFields), nameof(Fields.Provider.ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }
    }
}
