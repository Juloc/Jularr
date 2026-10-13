using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Operation;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Operation
{
    public static class OperationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField OperationTypeId = new(typeof(OperationType));
        public static readonly EnumField OperationStatusTypeId = new(typeof(OperationStatusType));
        public static readonly LongField ProcessedCount = new() { Minimum = 0 };
        public static readonly StringField ErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField AttentionKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField AttentionUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Format = StringFormat.HttpUrl };
        public static readonly StringField SourceLinkUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Format = StringFormat.HttpUrl };
        public static readonly DateTimeField DismissedAt = new() { Required = false };
        public static readonly BoolField IsFinished = new();
        public static readonly EnumField ActivityTabTypeId = new(typeof(ActivityTabType));
        public static readonly StringField Title = new() { MaximumLength = 1000 };
        public static readonly IntField ProgressPercent = new() { Minimum = 0, Maximum = 100, Required = false };
        public static readonly LongField SizeBytes = new() { Minimum = 0, Required = false };
        public static readonly LongField RemainingBytes = new() { Minimum = 0, Required = false };
        public static readonly IntField EtaSeconds = new() { Minimum = 0, Required = false };
        public static readonly EnumField DownloadStateTypeId = new(typeof(DownloadStateType)) { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField StartedAt = new() { Required = false };
        public static readonly DateTimeField FinishedAt = new() { Required = false };
    }
}
