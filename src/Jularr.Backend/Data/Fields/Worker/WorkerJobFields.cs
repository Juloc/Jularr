using Jularr.Backend.Data.Enums.Worker;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Worker
{
    public static class WorkerJobFields
    {
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
        public static readonly EnumField WorkerGroupTypeId = new(typeof(WorkerGroupType));
        public static readonly EnumField WorkerJobStateTypeId = new(typeof(WorkerJobStateType));
        public static readonly EnumField ResultTypeId = new(typeof(WorkerJobResultType));
        public static readonly EnumField LastResultTypeId = new(typeof(WorkerJobResultType)) { Required = false };
        public static readonly BoolField IsPaused = new();
        public static readonly BoolField PausesWhileUsed = new();
        public static readonly BoolField PausesWhileUsedChoice = new() { Required = false };
        public static readonly BoolField CanPauseWhileUsed = new();
        public static readonly BoolField CanRunNow = new();
        public static readonly IntField IntervalSeconds = new() { Minimum = 1, Required = false };
        public static readonly DateTimeField StartedAt = new() { Required = false };
        public static readonly DateTimeField FinishedAt = new();
        public static readonly DateTimeField LastStartedAt = new() { Required = false };
        public static readonly DateTimeField LastFinishedAt = new() { Required = false };
        public static readonly DateTimeField NextRunAt = new() { Required = false };
        public static readonly StringField ErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField LastErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
    }
}
