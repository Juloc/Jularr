using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
    public sealed class ReadDownloadStorageParametersV1 : BaseParameters
    {
    }

    public sealed class DownloadStorageDetailsV1 : BaseResponse
    {
        public DownloadStorageDetailsV1(string? usenetDownloadPath, string? directSourceDownloadPath, long usenetMaxJobBytes, bool allowDirectDownloadOnHostFilesystem)
        {
            UsenetDownloadPath = usenetDownloadPath;
            DirectSourceDownloadPath = directSourceDownloadPath;
            UsenetMaxJobBytes = usenetMaxJobBytes;
            AllowDirectDownloadOnHostFilesystem = allowDirectDownloadOnHostFilesystem;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.Path), false)]
        public string? UsenetDownloadPath { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.Path), false)]
        public string? DirectSourceDownloadPath { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.ConfiguredJobLimitBytes))]
        public long UsenetMaxJobBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.AllowsHostFilesystem))]
        public bool AllowDirectDownloadOnHostFilesystem { get; }
    }
}
