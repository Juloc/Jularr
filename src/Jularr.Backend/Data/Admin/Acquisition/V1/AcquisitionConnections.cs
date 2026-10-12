using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadAcquisitionConnectionsParametersV1 : BaseParameters
    {
        public ReadAcquisitionConnectionsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class AcquisitionConnectionParametersV1 : BaseParameters
    {
        public AcquisitionConnectionParametersV1(long acquisitionConnectionId)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }
    }

    public sealed class DeleteAcquisitionConnectionParametersV1 : BaseParameters
    {
        public DeleteAcquisitionConnectionParametersV1(long acquisitionConnectionId, long version)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            Version = version;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateAcquisitionConnectionParametersV1 : BaseParameters
    {
    }

    public sealed class AcquisitionConnectionDetailsV1 : BaseResponse
    {
        public AcquisitionConnectionDetailsV1(long id, string name, AcquisitionConnectionType acquisitionConnectionTypeId, string baseUrl, bool hasApiKey, bool isEnabled, int priority,
            int[] categories, string? downloadCategory, string? remotePath, string? localPath, DateTime? checkedAt, string? errorKey, long version)
        {
            Id = id;
            Name = name;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
            HasApiKey = hasApiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Categories = categories;
            DownloadCategory = downloadCategory;
            RemotePath = remotePath;
            LocalPath = localPath;
            CheckedAt = checkedAt;
            ErrorKey = errorKey;
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

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.HasApiKey))]
        public bool HasApiKey { get; }

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

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.CheckedAt))]
        public DateTime? CheckedAt { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Version))]
        public long Version { get; }
    }

    public sealed class SaveAcquisitionConnectionDataV1 : BaseData
    {
        public SaveAcquisitionConnectionDataV1(string name, AcquisitionConnectionType acquisitionConnectionTypeId, string baseUrl, string? apiKey, bool isEnabled, int priority, int[] categories,
            string? downloadCategory, string? remotePath, string? localPath)
        {
            Name = name;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Categories = categories;
            DownloadCategory = downloadCategory;
            RemotePath = remotePath;
            LocalPath = localPath;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ApiKey))]
        public string? ApiKey { get; }

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

        public override void Validate()
        {
            base.Validate();

            // Download settings belong to download clients; mappings are complete pairs.
            if (AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd && (DownloadCategory is not null || RemotePath is not null || LocalPath is not null))
            {
                throw new DataValidationException(nameof(DownloadCategory), "invalid_transition");
            }

            if ((RemotePath is null) != (LocalPath is null))
            {
                throw new DataValidationException(RemotePath is null ? nameof(RemotePath) : nameof(LocalPath), "required");
            }
        }
    }

    public sealed class CreateAcquisitionConnectionResultV1 : BaseResponse
    {
        public CreateAcquisitionConnectionResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }
    }

    public sealed class AcquisitionConnectionTestV1 : BaseResponse
    {
        public AcquisitionConnectionTestV1(bool isAvailable, string? errorKey, string? serverVersion, string[] searchModes)
        {
            IsAvailable = isAvailable;
            ErrorKey = errorKey;
            ServerVersion = serverVersion;
            SearchModes = searchModes;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ServerVersion))]
        public string? ServerVersion { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.SearchModes))]
        public string[] SearchModes { get; }
    }
}
