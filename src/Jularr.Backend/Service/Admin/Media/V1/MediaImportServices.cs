using System.Globalization;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // What an import would do, without changing anything: the list, matched to the library and proposed from the providers.
    [ServiceEndpoint("POST", "/api/v1/admin/media-import/preview")]
    public sealed class PreviewMediaImportV1 : AdminService<MediaImportParametersV1, MediaImportDataV1>
    {
        private const int SearchesPerPreview = 60;
        private readonly MediaImportPlanner _planner;

        // Optional request-scoped status events for the interactive preview.
        public Func<int, string, int?, string?, string?, string?, Task>? OnItemProgressAsync { get; set; }

        internal PreviewMediaImportV1(MediaImportPlanner planner)
        {
            _planner = planner;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportPreviewV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var json = await MediaImportText.ReadAsync(Data, CancellationToken);
            var items = MediaImportParser.Parse(json);
            // Bulk lists can be queued for durable per-item discovery; keep the
            // interactive preview short instead of spending minutes on Open Library.
            var searchBudget = items.Count >= 100 ? 8 : SearchesPerPreview;
            var plan = await _planner.PlanAsync(Context, items, Data, searchBudget, CancellationToken, OnItemProgressAsync);
            var profiles = await Context.ExecuteBatchAsync((ctx, ct) => MediaImportQueries.ReadProfilesAsync(ctx, ct), CancellationToken);
            var libraries = await Context.ExecuteBatchAsync((ctx, ct) => MediaImportQueries.ReadLibrariesAsync(ctx, ct), CancellationToken);
            var rows = plan.Rows.Select(row => new MediaImportRowV1(row.Item.Index, row.Item.TypeKey, row.Item.Type, row.Item.Subtype, row.Item.Title, row.Item.Year, row.Item.Creator, row.Status, row.Problem, row.ProblemDetail,
                row.WorkId, row.MatchTitle, row.MatchYear, row.MatchSource, row.Choice ?? (row.Import is { } import ? import.Namespace + ":" + import.Value : null), row.HasTarget, row.CanMonitor, row.IsIncluded,
                row.Query, row.DuplicateOf, new BaseDataList<MediaImportCandidateV1>(row.Candidates), row.Action, MediaImportPlanner.CanUseListData(row.Item)));
            return new MediaImportPreviewV1(json, new BaseDataList<MediaImportRowV1>(rows), new BaseDataList<MediaImportOptionV1>(profiles), new BaseDataList<MediaImportOptionV1>(libraries), plan.Unsearched, plan.ProviderFailure);
        }
    }

    // On-demand comparison for ambiguous book choices. The importer never uses
    // display-only edition details as another Work identity.
    [ServiceEndpoint("GET", "/api/v1/admin/media-import/editions/{workId}")]
    public sealed class ReadMediaImportEditionsV1 : AdminService<MediaImportEditionParametersV1>
    {
        private readonly BookMetadata _books;
        internal ReadMediaImportEditionsV1(BookMetadata books) => _books = books;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportEditionsV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportEditionParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportEditionParametersV1 parameters, Type resultType) => new("media.manual.update");
        protected override async Task<IServiceOutput> OnRead()
        {
            var editions = await _books.ReadEditionsAsync(Parameters.WorkId, CancellationToken);
            return new MediaImportEditionsV1(new BaseDataList<MediaImportEditionV1>(editions
                .OrderBy(edition => edition.Year ?? int.MaxValue).ThenBy(edition => edition.Title).Take(30)
                .Select(edition => new MediaImportEditionV1(
                    TextLimit.Cut(edition.Title, 500), TextLimit.Cut(edition.Language, 50),
                    TextLimit.Cut(edition.PhysicalFormat, 500), TextLimit.Cut(edition.Publisher, 500),
                    edition.Year is >= 1800 and <= 2200 ? edition.Year : null,
                    edition.Isbns.FirstOrDefault(isbn => isbn.Length <= 20)))));
        }
    }

    // The import itself: every ticked row is added from its provider entry and, as chosen, monitored and searched. Repeating a list changes nothing that is already there.
    [ServiceEndpoint("POST", "/api/v1/admin/media-import")]
    public sealed class RunMediaImportV1 : AdminService<MediaImportParametersV1, MediaImportDataV1>
    {
        // Each new title reads its provider; a run takes as many as a request can bear and leaves the rest for the next one.
        private const int NewTitlesPerRun = 40;
        private readonly MediaImportPlanner _planner;
        private readonly MediaImportProcessor _processor;
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _searches;

        // Optional request-scoped progress sink for the interactive import page; the normal API stays unchanged.
        public Func<MediaImportResultRowV1, Task>? OnRowProcessedAsync { get; set; }

        internal RunMediaImportV1(MediaImportPlanner planner, MediaImportProcessor processor, AcquisitionSearchSignal searchSignal)
        {
            _planner = planner;
            _processor = processor;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportResultV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Everything is read and matched before anything is written; the rows are then written one by one, each its own step.
            var items = MediaImportParser.Parse(await MediaImportText.ReadAsync(Data, CancellationToken));
            var plan = await _planner.PlanAsync(Context, items, Data, 0, CancellationToken);
            var results = new List<MediaImportResultRowV1>(plan.Rows.Count);
            var remaining = new List<MediaImportItem>();
            var unavailable = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            foreach (var row in plan.Rows)
            {
                MediaImportResultRowV1 result;
                if (row.IsIncluded && row.WorkId is null && row.Import is { } import
                    && (added >= NewTitlesPerRun || unavailable.Contains(import.Namespace)))
                {
                    result = new MediaImportResultRowV1(row.Item.Index, row.MatchTitle ?? row.Item.Title, MediaImportOutcomeType.Deferred, false, null);
                    remaining.Add(row.Item with { Ids = [import] });
                }
                else
                {
                    var processed = await _processor.ProcessAsync(Context, row, CancellationToken);
                    result = processed.Result;
                    if (processed.Created) added++;
                    if (result.OutcomeTypeId == MediaImportOutcomeType.Unavailable && row.Import is { } missing)
                    {
                        unavailable.Add(missing.Namespace);
                        remaining.Add(row.Item with { Ids = [missing] });
                    }
                    _searches |= result.Searches;
                }

                results.Add(result);
                if (OnRowProcessedAsync is { } notify) await notify(result);
            }

            // Each row is named as the library names its title.
            var ids = results.Select(row => row.WorkId).OfType<long>().Distinct().ToArray();
            var titles = ids.Length == 0 ? [] : (await Context.ExecuteBatchAsync((ctx, ct) => MediaImportQueries.ReadTitlesAsync(ctx, new MediaImportWorkLookupData(ids), ct), CancellationToken)).ToDictionary(work => work.Id, work => work.Title);
            var named = results.Select(row => new MediaImportResultRowV1(row.Index, row.WorkId is { } id && titles.TryGetValue(id, out var title) ? title : row.Title, row.OutcomeTypeId, row.Searches, row.WorkId));
            return new MediaImportResultV1(new BaseDataList<MediaImportResultRowV1>(named), remaining.Count > 0 ? MediaImportParser.Write(remaining) : null);
        }

        protected override void OnCommitted()
        {
            if (_searches) _searchSignal.RequestSearch();
        }
    }

    // The list as text, from a file or as pasted.
    internal static class MediaImportText
    {
        public static async Task<string> ReadAsync(MediaImportDataV1 data, CancellationToken ct)
        {
            if (data.File is not { } file) return data.Json!;

            // A file is read as strict UTF-8 and never beyond the limit.
            await using var stream = file.OpenRead();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var text = new StringBuilder();
            var buffer = new char[8192];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    text.Append(buffer, 0, count);
                    if (text.Length > MediaImportFields.MaximumBytes) throw new DataValidationException(nameof(data.File), "import_too_large");
                }
            }
            catch (DecoderFallbackException)
            {
                throw new DataValidationException(nameof(data.File), "invalid_text");
            }

            return text.ToString();
        }
    }
}
