using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.System.V1
{
    // The newest messages first; every filter narrows what is shown.
    public sealed class ReadLogsParametersV1 : BaseParameters
    {
        public ReadLogsParametersV1(LogLevelType? level = null, LogSourceType? source = null, string? search = null, int page = 1, int pageSize = 50)
        {
            Level = level;
            Source = source;
            Search = search;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(LogFields), nameof(LogFields.LevelFilter))]
        public LogLevelType? Level { get; }

        [DataField(typeof(LogFields), nameof(LogFields.SourceFilter))]
        public LogSourceType? Source { get; }

        [DataField(typeof(LogFields), nameof(LogFields.Search))]
        public string? Search { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // One message. The origin is the kind of event, the job or the application's area; the message of a job is its error key.
    public sealed class LogEntryV1 : BaseResponse
    {
        public LogEntryV1(DateTime at, LogLevelType level, LogSourceType source, string origin, string message)
        {
            At = at;
            Level = level;
            Source = source;
            Origin = origin;
            Message = message;
        }

        [DataField(typeof(LogFields), nameof(LogFields.At))]
        public DateTime At { get; }

        [DataField(typeof(LogFields), nameof(LogFields.Level))]
        public LogLevelType Level { get; }

        [DataField(typeof(LogFields), nameof(LogFields.Source))]
        public LogSourceType Source { get; }

        [DataField(typeof(LogFields), nameof(LogFields.Origin))]
        public string Origin { get; }

        [DataField(typeof(LogFields), nameof(LogFields.Message))]
        public string Message { get; }
    }
}
