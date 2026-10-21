using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.Admin.Transcoding.V1
{
    public sealed class TranscodeSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class TranscodeBenchmarkParametersV1 : BaseParameters
    {
    }

    public sealed class TranscodeSettingsV1 : BaseResponse
    {
        public TranscodeSettingsV1(TranscodeAcceleration transcodeAccelerationTypeId, int maxTranscodes, int[] ladderHeights, long version)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            MaxTranscodes = maxTranscodes;
            LadderHeights = ladderHeights;
            Version = version;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        // Conversions at once, counted in 1080p streams; a smaller picture takes less of it.
        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.MaxTranscodes))]
        public int MaxTranscodes { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.LadderHeights))]
        public int[] LadderHeights { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateTranscodeSettingsDataV1 : BaseData
    {
        public UpdateTranscodeSettingsDataV1(TranscodeAcceleration transcodeAccelerationTypeId, int maxTranscodes, int[] ladderHeights, long version)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            MaxTranscodes = maxTranscodes;
            LadderHeights = ladderHeights;
            Version = version;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.MaxTranscodes))]
        public int MaxTranscodes { get; }

        // Lower picture heights offered when the server is busy; none offers no lower picture.
        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.LadderHeights))]
        public int[] LadderHeights { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Version))]
        public long Version { get; }
    }

    // One way to convert video and whether this machine offers it.
    public sealed class TranscodeHardwareV1 : BaseResponse
    {
        public TranscodeHardwareV1(TranscodeAcceleration transcodeAccelerationTypeId, string encoder, string? device, bool isAvailable)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            Encoder = encoder;
            Device = device;
            IsAvailable = isAvailable;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Encoder))]
        public string Encoder { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Device))]
        public string? Device { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.IsAvailable))]
        public bool IsAvailable { get; }
    }

    // Seconds of picture one pipeline converted per second of work; none when it did not run.
    public sealed class TranscodeBenchmarkResultV1 : BaseResponse
    {
        public TranscodeBenchmarkResultV1(TranscodeAcceleration transcodeAccelerationTypeId, int height, decimal? speedFactor)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            Height = height;
            SpeedFactor = speedFactor;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Height))]
        public int Height { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.SpeedFactor))]
        public decimal? SpeedFactor { get; }
    }

    // The last benchmark with what it proposes, the machine's encoders and whether another run is queued or running.
    public sealed class TranscodeBenchmarkV1 : BaseResponse
    {
        public TranscodeBenchmarkV1(DateTime? ranAt, int? cpuCores, TranscodeAcceleration? bestTranscodeAccelerationTypeId, int? recommendedMaxTranscodes, bool isRunning, string? errorKey,
            BaseDataList<TranscodeHardwareV1> hardware, BaseDataList<TranscodeBenchmarkResultV1> results)
        {
            RanAt = ranAt;
            CpuCores = cpuCores;
            BestTranscodeAccelerationTypeId = bestTranscodeAccelerationTypeId;
            RecommendedMaxTranscodes = recommendedMaxTranscodes;
            IsRunning = isRunning;
            ErrorKey = errorKey;
            Hardware = hardware;
            Results = results;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.RanAt))]
        public DateTime? RanAt { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.CpuCores))]
        public int? CpuCores { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.BestTranscodeAccelerationTypeId))]
        public TranscodeAcceleration? BestTranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.RecommendedMaxTranscodes))]
        public int? RecommendedMaxTranscodes { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.IsRunning))]
        public bool IsRunning { get; }

        // The latest run failed with this reason.
        [DataField(typeof(Fields.Operation.OperationFields), nameof(Fields.Operation.OperationFields.ErrorKey))]
        public string? ErrorKey { get; }

        public BaseDataList<TranscodeHardwareV1> Hardware { get; }

        public BaseDataList<TranscodeBenchmarkResultV1> Results { get; }
    }
}
