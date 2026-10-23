using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    // Read-only file facts from the download client; never return filesystem write paths or credentials.
    public sealed class TorrentFileV1(string name, long sizeBytes, int progressPercent, bool isSelected) : BaseResponse
    {
        public string Name { get; } = name;
        public long SizeBytes { get; } = sizeBytes;
        public int ProgressPercent { get; } = progressPercent;
        public bool IsSelected { get; } = isSelected;
    }
}
