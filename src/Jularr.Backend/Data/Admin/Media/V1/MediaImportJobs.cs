using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // API clients submit explicit decisions as JSON. The Razor upload adapter
    // still calls the canonical QueueMediaImportJobV1 with MediaImportDataV1.
    public sealed class MediaImportJobRequestV1 : BaseData
    {
        public MediaImportJobRequestV1(string json, MediaImportActionType action,
            BaseDataList<MediaImportDefaultV1>? defaults = null,
            BaseDataList<MediaImportChoiceV1>? choices = null)
        {
            Json = json;
            Action = action;
            Defaults = defaults ?? new([]);
            Choices = choices ?? new([]);
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Json))]
        public string Json { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Action))]
        public MediaImportActionType Action { get; }

        public BaseDataList<MediaImportDefaultV1> Defaults { get; }
        public BaseDataList<MediaImportChoiceV1> Choices { get; }

        public override void Validate()
        {
            base.Validate();
            new MediaImportDataV1(null, Json, Action, Defaults, Choices).Validate();
        }
    }

    public sealed class MediaImportJobParametersV1 : BaseParameters
    {
        public MediaImportJobParametersV1(Guid jobId) => JobId = jobId;

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.JobId))]
        public Guid JobId { get; }
    }

    public sealed class MediaImportJobRowV1 : BaseResponse
    {
        public MediaImportJobRowV1(int index, string? title, string stage, MediaImportOutcomeType? outcomeTypeId, long? workId, bool searches)
        {
            Index = index;
            Title = title;
            Stage = stage;
            OutcomeTypeId = outcomeTypeId;
            WorkId = workId;
            Searches = searches;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Index))]
        public int Index { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Title { get; }

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.Stage))]
        public string Stage { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.OutcomeTypeId), false)]
        public MediaImportOutcomeType? OutcomeTypeId { get; }

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.WorkId), false)]
        public long? WorkId { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool Searches { get; }
    }

    public sealed class MediaImportJobStatusV1 : BaseResponse
    {
        public MediaImportJobStatusV1(Guid jobId, OperationStatusType statusTypeId, int totalCount, long processedCount, long sequence, string? errorKey,
            BaseDataList<MediaImportJobRowV1> rows)
        {
            JobId = jobId;
            StatusTypeId = statusTypeId;
            TotalCount = totalCount;
            ProcessedCount = processedCount;
            Sequence = sequence;
            ErrorKey = errorKey;
            Rows = rows;
        }

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.JobId))]
        public Guid JobId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType StatusTypeId { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Count))]
        public int TotalCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProcessedCount))]
        public long ProcessedCount { get; }

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.Sequence))]
        public long Sequence { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey), false)]
        public string? ErrorKey { get; }

        public BaseDataList<MediaImportJobRowV1> Rows { get; }
        public override void Validate()
        {
            base.Validate();
            Rows.Validate(nameof(Rows));
        }
    }

    public sealed class MediaImportLatestJobV1 : BaseResponse
    {
        public MediaImportLatestJobV1(Guid? jobId) => JobId = jobId;

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.LatestJobId), false)]
        public Guid? JobId { get; }
    }

    public sealed class MediaImportJobCreatedV1 : BaseResponse
    {
        public MediaImportJobCreatedV1(Guid jobId) => JobId = jobId;

        [DataField(typeof(MediaImportJobFields), nameof(MediaImportJobFields.JobId))]
        public Guid JobId { get; }
    }
}
