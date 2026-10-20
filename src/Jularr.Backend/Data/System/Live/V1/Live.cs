using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Live;

namespace Jularr.Backend.Data.System.Live.V1
{
    public sealed class ReadLiveVersionParametersV1 : BaseParameters
    {
    }

    // A fingerprint of everything the live views show, and whether a download or another job runs now.
    public sealed class LiveVersionV1 : BaseResponse
    {
        public LiveVersionV1(string version, bool hasDownloads, bool hasJobs)
        {
            Version = version;
            HasDownloads = hasDownloads;
            HasJobs = hasJobs;
        }

        [DataField(typeof(LiveFields), nameof(LiveFields.Version))]
        public string Version { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.HasDownloads))]
        public bool HasDownloads { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.HasJobs))]
        public bool HasJobs { get; }
    }
}
