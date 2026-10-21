using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.Logic.Playback
{
    // The stored choices; no maximum follows the configuration.
    internal sealed class TranscodeSettingsData : BaseData
    {
        public TranscodeSettingsData(TranscodeAcceleration transcodeAccelerationTypeId, int? maxTranscodes, int[] ladderHeights, long version)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            MaxTranscodes = maxTranscodes;
            LadderHeights = ladderHeights;
            Version = version;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.MaxTranscodes), false)]
        public int? MaxTranscodes { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.LadderHeights))]
        public int[] LadderHeights { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Version))]
        public long Version { get; }
    }

    internal sealed class TranscodeSettingsUpdateData : BaseData
    {
        public TranscodeSettingsUpdateData(TranscodeAcceleration transcodeAccelerationTypeId, int maxTranscodes, int[] ladderHeights, long version)
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

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.LadderHeights))]
        public int[] LadderHeights { get; set; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Version))]
        public long Version { get; }
    }

    // What a playback plan needs: the choices, the benchmark's best pipeline and the caller's priority.
    internal sealed class TranscodeLimitsData : BaseData
    {
        public TranscodeLimitsData(TranscodeAcceleration transcodeAccelerationTypeId, int? maxTranscodes, int[] ladderHeights, TranscodeAcceleration? bestTranscodeAccelerationTypeId, bool transcodingPriority)
        {
            TranscodeAccelerationTypeId = transcodeAccelerationTypeId;
            MaxTranscodes = maxTranscodes;
            LadderHeights = ladderHeights;
            BestTranscodeAccelerationTypeId = bestTranscodeAccelerationTypeId;
            TranscodingPriority = transcodingPriority;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.TranscodeAccelerationTypeId))]
        public TranscodeAcceleration TranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.MaxTranscodes), false)]
        public int? MaxTranscodes { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.LadderHeights))]
        public int[] LadderHeights { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.BestTranscodeAccelerationTypeId), false)]
        public TranscodeAcceleration? BestTranscodeAccelerationTypeId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.TranscodingPriority))]
        public bool TranscodingPriority { get; }
    }

    // The account whose conversion is planned.
    internal sealed class TranscodeAccountData : BaseData
    {
        public TranscodeAccountData(long id)
        {
            Id = id;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; }
    }

    // A finished benchmark; the results are a JSON list of acceleration, height and speed.
    internal sealed class TranscodeBenchmarkSaveData : BaseData
    {
        public TranscodeBenchmarkSaveData(int cpuCores, TranscodeAcceleration bestTranscodeAccelerationTypeId, int recommendedMaxTranscodes, string results)
        {
            CpuCores = cpuCores;
            BestTranscodeAccelerationTypeId = bestTranscodeAccelerationTypeId;
            RecommendedMaxTranscodes = recommendedMaxTranscodes;
            Results = results;
        }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.CpuCores))]
        public int CpuCores { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.BestTranscodeAccelerationTypeId))]
        public TranscodeAcceleration BestTranscodeAccelerationTypeId { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.RecommendedMaxTranscodes))]
        public int RecommendedMaxTranscodes { get; }

        [DataField(typeof(TranscodeFields), nameof(TranscodeFields.Results))]
        public string Results { get; }
    }

    internal sealed class TranscodeBenchmarkRowData : BaseData
    {
        public TranscodeBenchmarkRowData(DateTime? ranAt, int? cpuCores, TranscodeAcceleration? bestTranscodeAccelerationTypeId, int? recommendedMaxTranscodes, bool isRunning, string? errorKey)
        {
            RanAt = ranAt;
            CpuCores = cpuCores;
            BestTranscodeAccelerationTypeId = bestTranscodeAccelerationTypeId;
            RecommendedMaxTranscodes = recommendedMaxTranscodes;
            IsRunning = isRunning;
            ErrorKey = errorKey;
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

        [DataField(typeof(Fields.Operation.OperationFields), nameof(Fields.Operation.OperationFields.ErrorKey))]
        public string? ErrorKey { get; }
    }
}
