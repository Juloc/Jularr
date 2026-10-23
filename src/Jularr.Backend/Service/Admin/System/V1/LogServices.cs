using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.System.V1
{
    // The messages of the last days, newest first: what happened to the media, the background jobs that failed, and, while the Diagnostics module is on,
    // the application's own warnings and errors since the start (a bounded buffer in memory, exception type and message, never a stack trace).
    [ServiceEndpoint("GET", "/api/v1/admin/system/logs")]
    public sealed class ReadLogsV1 : AdminService<ReadLogsParametersV1>
    {
        private readonly RecentLogs _logs;

        internal ReadLogsV1(RecentLogs logs)
        {
            _logs = logs;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LogEntryV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLogsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadLogsParametersV1 parameters, Type resultType) => new("instance.settings.read");
        protected override PageRequest GetPageRequest(ReadLogsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Each source gives its newest entries up to the end of the page; together they hold the page.
            var take = (int)(Page.Offset + Page.PageSize);
            var entries = new List<LogEntryV1>();
            if (Parameters.Source != LogSourceType.Application)
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        entry."At",
                        entry."Level",
                        entry."Source",
                        entry."Origin",
                        entry."Message"
                    FROM
                        (
                            SELECT
                                event."OccurredAt" AS "At",
                                CASE WHEN event."ActivityEventTypeId" = 2 THEN 2 ELSE 1 END AS "Level",
                                1 AS "Source",
                                kind."Key" AS "Origin",
                                left(event."WorkTitle" || coalesce(' · ' || event."ReleaseTitle", ''), 4000) AS "Message"
                            FROM
                                "ActivityEvents" AS event
                                JOIN "ActivityEventTypes" AS kind ON kind."Id" = event."ActivityEventTypeId"
                            UNION ALL
                            SELECT
                                coalesce(operation."FinishedAt", operation."CreatedAt") AS "At",
                                3 AS "Level",
                                2 AS "Source",
                                operation."WorkerJobKey" AS "Origin",
                                operation."ErrorKey" AS "Message"
                            FROM
                                "Operations" AS operation
                            WHERE
                                operation."OperationTypeId" = 4
                                AND operation."ErrorKey" IS NOT NULL
                        ) AS entry
                    WHERE
                        (@Source::smallint IS NULL OR entry."Source" = @Source)
                        AND (@Level::smallint IS NULL OR entry."Level" = @Level)
                        AND (@Search::text IS NULL OR strpos(lower(entry."Message"), lower(@Search)) > 0 OR strpos(lower(entry."Origin"), lower(@Search)) > 0)
                    ORDER BY
                        entry."At" DESC
                    LIMIT @Take;
                    """;
                sql.Parameters.Add(Parameters);
                sql.Parameters.Add(new LogTakeData(take));
                entries.AddRange(await Context.ReadSql.ReadListAsync<LogEntryV1>(sql, CancellationToken));
            }

            if ((Parameters.Source is null || Parameters.Source == LogSourceType.Application) && _logs.IsEnabled && Parameters.Level != LogLevelType.Info)
            {
                entries.AddRange(_logs.Read(int.MaxValue).Select(entry => new LogEntryV1(entry.At, entry.Level >= LogLevel.Error ? LogLevelType.Error : LogLevelType.Warning, LogSourceType.Application,
                        entry.Category, entry.Message))
                    .Where(entry => Parameters.Level is null || entry.Level == Parameters.Level)
                    .Where(entry => Parameters.Search is not { Length: > 0 } search || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Origin.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            return new PageResult<LogEntryV1>([.. entries.OrderByDescending(entry => entry.At).Skip((int)Page.Offset).Take(Page.PageSize)], Page);
        }
    }
}
