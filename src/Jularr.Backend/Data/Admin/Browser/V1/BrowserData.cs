using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Browser.V1
{
    public sealed class BrowserParametersV1 : BaseParameters
    {
    }

    public sealed class BrowserEventV1 : BaseResponse
    {
        public BrowserEventV1(DateTime at, string kind, string message)
        {
            At = at;
            Kind = kind;
            Message = message;
        }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.At))]
        public DateTime At { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Kind))]
        public string Kind { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Message))]
        public string Message { get; }
    }

    public sealed class BrowserStatusV1 : BaseResponse
    {
        public BrowserStatusV1(string? path, string? remoteAddress, string profilePath, bool isAvailable, bool isRunning, string? version, string? lastUrl, string? lastOutcome, string? lastError,
            DateTime? lastAttemptAt, PageResult<BrowserEventV1> events, string installationState = "disabled", string? installationError = null,
            long downloadedBytes = 0, long totalBytes = 0, string? installedVersion = null, long managedSizeBytes = 0, BrowserViewV1? manualView = null)
        {
            Path = path;
            RemoteAddress = remoteAddress;
            ProfilePath = profilePath;
            IsAvailable = isAvailable;
            IsRunning = isRunning;
            Version = version;
            LastUrl = lastUrl;
            LastOutcome = lastOutcome;
            LastError = lastError;
            LastAttemptAt = lastAttemptAt;
            Events = events;
            InstallationState = installationState;
            InstallationError = installationError;
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
            InstalledVersion = installedVersion;
            ManagedSizeBytes = managedSizeBytes;
            ManualView = manualView;
        }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.InstallationState))]
        public string InstallationState { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.InstallationError), false)]
        public string? InstallationError { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.DownloadedBytes))]
        public long DownloadedBytes { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.TotalBytes))]
        public long TotalBytes { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.InstalledVersion), false)]
        public string? InstalledVersion { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.ManagedSizeBytes))]
        public long ManagedSizeBytes { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Path), false)]
        public string? Path { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.RemoteAddress), false)]
        public string? RemoteAddress { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.ProfilePath))]
        public string ProfilePath { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.IsRunning))]
        public bool IsRunning { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Version), false)]
        public string? Version { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.LastUrl), false)]
        public string? LastUrl { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.LastOutcome), false)]
        public string? LastOutcome { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.LastError), false)]
        public string? LastError { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.LastAttemptAt), false)]
        public DateTime? LastAttemptAt { get; }

        public PageResult<BrowserEventV1> Events { get; }
        public BrowserViewV1? ManualView { get; }
    }

    public sealed class RunBrowserActionDataV1 : BaseData
    {
        public RunBrowserActionDataV1(string action, string? url = null)
        {
            Action = action;
            Url = url;
        }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Action))]
        public string Action { get; }

        [DataField(typeof(BrowserFields), nameof(BrowserFields.Url), false)]
        public string? Url { get; }
    }
}
