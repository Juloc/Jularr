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
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField StartedAt = new() { Required = false };
        public static readonly DateTimeField FinishedAt = new() { Required = false };
    }
}
