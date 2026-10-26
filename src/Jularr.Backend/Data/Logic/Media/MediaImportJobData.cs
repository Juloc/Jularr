using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed record MediaImportJobPayload(string Json, MediaImportActionType Action,
        MediaImportDefaultV1[] Defaults, MediaImportChoiceV1[] Choices);

    internal sealed record MediaImportJobQueueData(long AccountId, Guid PublicId, string Payload, int TotalCount);
    internal sealed record MediaImportJobOwnerData(long AccountId);
    internal sealed record MediaImportJobKeyData(Guid JobId, long AccountId);
    internal sealed record MediaImportJobOperationData(long OperationId);
    internal sealed record MediaImportJobCheckpointData(long OperationId, int RowIndex, MediaImportOutcomeType OutcomeTypeId, string? Title, long? WorkId, bool Searches);
    internal sealed record MediaImportJobIdData(Guid JobId);
    internal sealed record MediaImportJobStageData(long OperationId, int RowIndex, string Stage,
        string? Source = null, string? Query = null, string? Detail = null, int? CandidateCount = null,
        int Attempt = 0, string? MatchesJson = null);
    internal sealed record MediaImportJobPermissionData(bool Allowed);
    internal sealed record MediaImportJobWorkData(long OperationId, long RequestedByAccountId, string Payload, int TotalCount, int Cursor);
    internal sealed record MediaImportJobHeaderData(Guid JobId, OperationStatusType StatusTypeId, int TotalCount, long ProcessedCount, long Sequence, string? ErrorKey);
    internal sealed record MediaImportJobRowData(int Index, string? Title, string Stage, MediaImportOutcomeType? OutcomeTypeId, long? WorkId, bool Searches,
        string? Source, string? Query, string? Detail, int? CandidateCount, int Attempt, string HistoryJson);

}
