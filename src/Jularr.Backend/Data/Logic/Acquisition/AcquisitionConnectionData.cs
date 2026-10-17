using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionConnectionCategoryMapData : BaseData
    {
        public AcquisitionConnectionCategoryMapData(long acquisitionConnectionId, string categoryMap)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            CategoryMap = categoryMap;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategoryMap))]
        public string CategoryMap { get; }
    }

    internal sealed class AcquisitionConnectionData : BaseData
    {
        public AcquisitionConnectionData(long id, string name, AcquisitionConnectionType acquisitionConnectionTypeId, string? baseUrl, byte[]? protectedApiKey, bool isEnabled, int priority,
            int[] categories, string? downloadCategory, string? remotePath, string? localPath, string? capabilities, long version, string? username,
            int? maxConnections, byte[]? protectedPassword, int[] mediaTypeIds, bool searchesAutomatically, int? queryLimit, int? grabLimit, int queryCount, int grabCount, DateTime? retryAt,
            string? host, int? port, bool? useSsl, string? incompleteRemotePath = null, string? incompleteLocalPath = null, string? downloadCategoryMap = null)
        {
            Host = host;
            Port = port;
            UseSsl = useSsl;
            MediaTypeIds = mediaTypeIds;
            SearchesAutomatically = searchesAutomatically;
            QueryLimit = queryLimit;
            GrabLimit = grabLimit;
            QueryCount = queryCount;
            GrabCount = grabCount;
            RetryAt = retryAt;
            ProtectedPassword = protectedPassword;
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
            IncompleteRemotePath = incompleteRemotePath;
            IncompleteLocalPath = incompleteLocalPath;
            DownloadCategoryMap = downloadCategoryMap;
            Capabilities = capabilities;
            Version = version;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), false)]
        public string? BaseUrl { get; }

        // The HTTP address of every connection but a news server.
        public string HttpAddress => BaseUrl ?? throw new InvalidOperationException("A news server has no HTTP address.");

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Host))]
        public string? Host { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Port))]
        public int? Port { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.UseSsl), false)]
        public bool? UseSsl { get; }

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

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IncompleteRemotePath))]
        public string? IncompleteRemotePath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IncompleteLocalPath))]
        public string? IncompleteLocalPath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategoryMap))]
        public string? DownloadCategoryMap { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Capabilities))]
        public string? Capabilities { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }

        // OPDS catalogs and news servers may need an account; the password is the protected key.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Username))]
        public string? Username { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MaxConnections))]
        public int? MaxConnections { get; }

        [JsonIgnore]
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ProtectedPassword), false)]
        public byte[]? ProtectedPassword { get; }

        // Indexer participation; the counts are today's.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MediaTypeIds))]
        public int[] MediaTypeIds { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.SearchesAutomatically))]
        public bool SearchesAutomatically { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.QueryLimit))]
        public int? QueryLimit { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.GrabLimit))]
        public int? GrabLimit { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.QueryCount))]
        public int QueryCount { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.GrabCount))]
        public int GrabCount { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.RetryAt))]
        public DateTime? RetryAt { get; }
    }

    internal sealed class AcquisitionConnectionSaveData : BaseData
    {
        public AcquisitionConnectionSaveData(long? id, string name, AcquisitionConnectionType acquisitionConnectionTypeId, string? baseUrl, byte[]? protectedApiKey, bool isEnabled, int priority,
            int[] categories, string? downloadCategory, string? remotePath, string? localPath, string? username,
            int? maxConnections, byte[]? protectedPassword, int[] mediaTypeIds, bool searchesAutomatically, int? queryLimit, int? grabLimit, string? host = null, int? port = null,
            bool? useSsl = null, string? incompleteRemotePath = null, string? incompleteLocalPath = null)
        {
            Host = host;
            Port = port;
            UseSsl = useSsl;
            MediaTypeIds = mediaTypeIds;
            SearchesAutomatically = searchesAutomatically;
            QueryLimit = queryLimit;
            GrabLimit = grabLimit;
            ProtectedPassword = protectedPassword;
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
            IncompleteRemotePath = incompleteRemotePath;
            IncompleteLocalPath = incompleteLocalPath;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), false)]
        public string? BaseUrl { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Host))]
        public string? Host { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Port))]
        public int? Port { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.UseSsl), false)]
        public bool? UseSsl { get; }

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

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IncompleteRemotePath))]
        public string? IncompleteRemotePath { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IncompleteLocalPath))]
        public string? IncompleteLocalPath { get; }

        // OPDS catalogs and news servers may need an account; the password is the protected key.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Username))]
        public string? Username { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MaxConnections))]
        public int? MaxConnections { get; }

        [JsonIgnore]
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ProtectedPassword), false)]
        public byte[]? ProtectedPassword { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MediaTypeIds))]
        public int[] MediaTypeIds { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.SearchesAutomatically))]
        public bool SearchesAutomatically { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.QueryLimit))]
        public int? QueryLimit { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.GrabLimit))]
        public int? GrabLimit { get; }
    }

    // What one search asked of an indexer and whether it answered.
    internal sealed class IndexerUseData : BaseData
    {
        public IndexerUseData(long acquisitionConnectionId, int queryCount, bool isAvailable)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            QueryCount = queryCount;
            IsAvailable = isAvailable;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.QueryCount))]
        public int QueryCount { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IsAvailable))]
        public bool IsAvailable { get; }
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
        public SourceConnectionData(AcquisitionConnectionType acquisitionConnectionTypeId, string? baseUrl)
        {
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), false)]
        public string? BaseUrl { get; }
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

    // The address of a configured connection; a takeover skips what already exists.
    internal sealed class AcquisitionConnectionUrlData : BaseData
    {
        public AcquisitionConnectionUrlData(string baseUrl)
        {
            BaseUrl = baseUrl;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }
    }

    internal sealed class AcquisitionConnectionRoleFilterData : BaseData
    {
        public AcquisitionConnectionRoleFilterData(AcquisitionConnectionRole? acquisitionConnectionRoleId)
        {
            AcquisitionConnectionRoleId = acquisitionConnectionRoleId;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionRoleId), false)]
        public AcquisitionConnectionRole? AcquisitionConnectionRoleId { get; }
    }

    internal sealed class AcquisitionConnectionRoleParameters : BaseData
    {
        public AcquisitionConnectionRoleParameters(AcquisitionConnectionRole acquisitionConnectionRoleId)
        {
            AcquisitionConnectionRoleId = acquisitionConnectionRoleId;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionRoleId))]
        public AcquisitionConnectionRole AcquisitionConnectionRoleId { get; }
    }
}
