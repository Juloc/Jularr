using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Operation;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Operation
{
    public static class OperationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongArrayField Ids = new() { Minimum = 1, MaximumLength = 64 };
        public static readonly LongArrayField QueueOrder = new() { Minimum = 1, MaximumLength = 10000 };
        public static readonly BoolField IsMissing = new();
        public static readonly EnumField OperationTypeId = new(typeof(OperationType));
        public static readonly EnumField OperationStatusTypeId = new(typeof(OperationStatusType));
        public static readonly LongField ProcessedCount = new() { Minimum = 0 };
        public static readonly IntField QueueCount = new() { Minimum = 0 };
        public static readonly StringField ErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField AttentionKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField HintKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField AttentionUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Format = StringFormat.HttpUrl };
        public static readonly StringField AttentionProposal = new() { Required = false };
        public static readonly StringField SourceLinkUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Format = StringFormat.HttpUrl };
        public static readonly DateTimeField DismissedAt = new() { Required = false };
        public static readonly BoolField IsFinished = new();
        public static readonly BoolField HasProposal = new();
        public static readonly BoolField BlocksRelease = new();
        public static readonly BoolField IsPaused = new();
        public static readonly BoolField CanPause = new();
        public static readonly BoolField TorrentOnly = new();
        public static readonly BoolField CanRetryImport = new();
        public static readonly BoolField IsWaiting = new();
        public static readonly BoolField CanImportManually = new();
        public static readonly StringArrayField UnmatchedFiles = new() { ItemMaximumLength = 2048, Required = false };
        public static readonly EnumField ActivityFilterTypeId = new(typeof(ActivityFilterType));
        public static readonly EnumField DownloadClientKindId = new(typeof(DownloadClientKind)) { Required = false };
        public static readonly EnumField DownloadBulkActionTypeId = new(typeof(DownloadBulkActionType));
        public static readonly BoolField DownloadsOnly = new();
        public static readonly BoolField FailedOnly = new();
        public static readonly EnumField DownloadPriorityTypeId = new(typeof(DownloadPriorityType));
        public static readonly IntField QueuePosition = new() { Minimum = 0, Required = false };
        public static readonly LongArrayField SpeedSamples = new() { Minimum = 0, MaximumLength = 120 };
        public static readonly StringField DownloadUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 4096, Format = StringFormat.SingleLine };
        public static readonly StringField Title = new() { MaximumLength = 1000 };
        public static readonly IntField ProgressPercent = new() { Minimum = 0, Maximum = 100, Required = false };
        public static readonly LongField SizeBytes = new() { Minimum = 0, Required = false };
        public static readonly LongField RemainingBytes = new() { Minimum = 0, Required = false };
        public static readonly IntField EtaSeconds = new() { Minimum = 0, Required = false };
        public static readonly EnumField DownloadStateTypeId = new(typeof(DownloadStateType)) { Required = false };
        public static readonly DateTimeField DownloadWaitUntil = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField StartedAt = new() { Required = false };
        public static readonly DateTimeField FinishedAt = new() { Required = false };
    }
}
