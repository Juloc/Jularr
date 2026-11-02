using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Common;
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

            if (!row.IsIncluded || row.Import is null && row.WorkId is null && !row.UsesListData)
            {
                return Result(MediaImportOutcomeType.Skipped);
            }

            var id = row.WorkId;
            var isNew = false;
            if (row.UsesListData)
            {
                if (checkOwnerAsync is not null) await checkOwnerAsync();
                var book = await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    // Only the short local creation is serialized; no provider I/O or long-running job holds this lock.
                    var gate = new SqlStatement { Statement = "SELECT pg_advisory_xact_lock(9461824);" };
                    await ctx.ExecuteAsyncForAffectedRows(gate, token);
                    var local = await MediaImportQueries.ReadLocalBooksAsync(ctx, [row.Item], token);
                    var identities = row.Item.Ids.SelectMany(identity => MediaImportParser.Isbn10(identity.Value) is { } ten
                        ? new[] { identity, new MediaImportId("isbn", ten) } : [identity]);
                    var known = await MediaImportQueries.ReadKnownAsync(ctx, new WorkIdentityLookupData(JsonSerializer.Serialize(identities)), token);
                    if (local.Count > 1 || known.Any(hit => local.Count != 1 || hit.WorkId != local[0].WorkId)) return (Id: (long?)null, Created: false);
                    var workId = local.Count == 1 ? local[0].WorkId
                        : (await ctx.CreateWorkAsync(new WorkCreationData(MediaType.Book, null, false, row.Item.Title!, row.Item.Year), token)).Id;
                    if (local.Count == 0)
                    {
                        var authors = row.Item.Authors ?? [row.Item.Creator!];
                        await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, "manual", WorkPeopleLogic.Rows(authors.Select(name => new ProviderPerson(null, name, ProviderPerson.Author)))), token);
                    }
                    foreach (var isbn in row.Item.Ids)
                        await ctx.SaveManualBookIsbnAsync(new WorkIdentityData("isbn", isbn.Value, workId), token);
                    return (Id: (long?)workId, Created: local.Count == 0);
                }, ct);
                if (book.Id is null) return Result(MediaImportOutcomeType.NeedsReview);
                id = book.Id;
                isNew = book.Created;
            }
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
