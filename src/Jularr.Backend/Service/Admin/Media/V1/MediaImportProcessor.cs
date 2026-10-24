using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    internal sealed record MediaImportRowExecution(MediaImportResultRowV1 Result, bool Created);

    // One owner for both the ordinary request path and durable background imports.
    // Provider I/O happens outside write transactions; Work/Wanted mutations use the
    // canonical Catalog/Logic methods and can be safely re-evaluated after a crash.
    internal sealed class MediaImportProcessor(MetadataCatalog catalog, ILogger<MediaImportProcessor> logger)
    {
        public async Task<MediaImportRowExecution> ProcessAsync(LogicContext context, MediaImportPlanRow row, CancellationToken ct,
            Func<Task>? checkOwnerAsync = null)
        {
            var title = row.MatchTitle ?? row.Item.Title;
            MediaImportRowExecution Result(MediaImportOutcomeType outcome, long? workId = null, bool searches = false, bool created = false) =>
                new(new MediaImportResultRowV1(row.Item.Index, title, outcome, searches, workId ?? row.WorkId), created);

            if (row.Status == MediaImportStatusType.Invalid && row.Problem == "excluded")
            {
                return Result(MediaImportOutcomeType.Excluded);
            }

            if (!row.IsIncluded || row.Import is null && row.WorkId is null)
            {
                return Result(MediaImportOutcomeType.Skipped);
            }

            var id = row.WorkId;
            var isNew = false;
            if (id is null)
            {
                var import = row.Import!;
                try
                {
                    if (checkOwnerAsync is not null) await checkOwnerAsync();
                    id = await catalog.ImportAsync(context, row.Item.Type!.Value, row.Item.Subtype, import.Namespace, import.Value, ct);
                    isNew = true;
                }
                catch (KeyNotFoundException)
                {
                    return Result(MediaImportOutcomeType.NotFound);
                }
                catch (ServiceUnavailableException)
                {
                    return Result(MediaImportOutcomeType.Unavailable);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && exception is not UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Media list import of {Namespace} {Value} failed.", import.Namespace, import.Value);
                    return Result(MediaImportOutcomeType.Failed);
                }
            }

            var monitored = false;
            if (row.Action != MediaImportActionType.AddOnly && row.CanMonitor)
            {
                try
                {
                    if (checkOwnerAsync is not null) await checkOwnerAsync();
                    monitored = await context.ExecuteBatchAsync((ctx, token) =>
                        ctx.EnsureWantedItemAsync(new WantedTakeoverData(id!.Value, row.AcquisitionProfileId, row.LibraryRootId, false), token), ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && exception is not UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Media list target of work {WorkId} failed.", id);
                    return Result(MediaImportOutcomeType.Failed, id, created: isNew);
                }
            }

            var search = monitored && row.Action == MediaImportActionType.MonitorAndSearch;
            return Result(isNew
                    ? monitored ? MediaImportOutcomeType.AddedAndMonitored : MediaImportOutcomeType.Added
                    : monitored ? MediaImportOutcomeType.Monitored : MediaImportOutcomeType.Unchanged,
                id, search, isNew);
        }
    }
}
