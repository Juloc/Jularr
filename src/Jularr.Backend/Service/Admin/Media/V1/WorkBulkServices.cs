using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // The mass editor of the library list. Every service takes the chosen titles, up to a bounded batch, and answers with what happened to each one.
    internal static class WorkBulkOutcomes
    {
        public static BaseDataList<WorkBulkOutcomeV1> Of(IEnumerable<WorkOutcomeData> outcomes) =>
            new(outcomes.Select(outcome => new WorkBulkOutcomeV1(outcome.Id, outcome.CanonicalTitle, outcome.OutcomeKey)));

        // A title whose action reaches an indexer, a provider or its files is left as it is when that fails, and the next one is tried.
        public static async Task<WorkOutcomeData> TryAsync(long id, string title, Func<Task<string>> action)
        {
            try
            {
                return new WorkOutcomeData(id, title, await action());
            }
            catch (DataValidationException exception)
            {
                return new WorkOutcomeData(id, title, exception.Code);
            }
            catch (ServiceUnavailableException exception)
            {
                return new WorkOutcomeData(id, title, exception.Code);
            }
            catch (KeyNotFoundException)
            {
                return new WorkOutcomeData(id, title, "not_found");
            }
        }
    }

    // Monitor or unmonitor: the target of each title is paused or resumed as on its own page.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/monitoring")]
    public sealed class UpdateWorksMonitoringV1 : AdminService<WorksBulkParametersV1, UpdateWorksMonitoringDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal UpdateWorksMonitoringV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var outcomes = await Context.ExecuteBatchAsync((ctx, ct) => ctx.SetWorksMonitoringAsync(new WorkMonitoringData(Data.WorkIds, Data.IsMonitored), ct), CancellationToken);

            // A title that became monitored is searched in the next cycle.
            if (Data.IsMonitored && outcomes.Any(outcome => outcome.OutcomeKey == "done")) _searchSignal.RequestSearch();
            return WorkBulkOutcomes.Of(outcomes);
        }
    }

    // The quality profile of the targets; without one the default of the type applies again.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/profile")]
    public sealed class UpdateWorksProfileV1 : AdminService<WorksBulkParametersV1, UpdateWorksProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute() => WorkBulkOutcomes.Of(await Context.ExecuteBatchAsync(
            (ctx, ct) => ctx.SetWorksProfileAsync(new WorkTargetChoiceData(Data.WorkIds, Data.AcquisitionProfileId, null), ct), CancellationToken));
    }

    // The library new files go to. No file moves: what is filed stays where it is.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/library")]
    public sealed class UpdateWorksLibraryV1 : AdminService<WorksBulkParametersV1, UpdateWorksLibraryDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute() => WorkBulkOutcomes.Of(await Context.ExecuteBatchAsync(
            (ctx, ct) => ctx.SetWorksLibraryAsync(new WorkTargetChoiceData(Data.WorkIds, null, Data.LibraryRootId), ct), CancellationToken));
    }

    // Search selected: each title is searched and its best release taken at once, as on its own page.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/search")]
    public sealed class SearchWorksV1 : AdminService<WorksBulkParametersV1, HeavyWorksDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal SearchWorksV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWorkBulkTargetsAsync(new WorkTargetChoiceData(Data.WorkIds, null, null), ct), CancellationToken);
            var outcomes = new List<WorkOutcomeData>();
            string? unavailable = null;
            foreach (var id in Data.WorkIds)
            {
                var target = targets.FirstOrDefault(item => item.Id == id);
                if (target is null) outcomes.Add(new WorkOutcomeData(id, "#" + id, "not_found"));
                else if (target.MediaTypeId == MediaType.Game) outcomes.Add(new WorkOutcomeData(id, target.CanonicalTitle, "per_platform"));
                else if (target.WantedItemId is not { } wantedItemId) outcomes.Add(new WorkOutcomeData(id, target.CanonicalTitle, "not_wanted"));
                else if (unavailable is not null) outcomes.Add(new WorkOutcomeData(id, target.CanonicalTitle, unavailable));
                else
                {
                    var outcome = await WorkBulkOutcomes.TryAsync(id, target.CanonicalTitle, async () => (await _acquisition.SearchAutomaticallyAsync(Context, wantedItemId, null, null, null, Auth.AccountId, CancellationToken)).OutcomeKey);
                    outcomes.Add(outcome);

                    // No indexer answers: the other titles are not tried against the same fault.
                    if (outcome.OutcomeKey is "acquisition_unavailable") unavailable = outcome.OutcomeKey;
                }
            }

            return WorkBulkOutcomes.Of(outcomes);
        }
    }

    // Refresh metadata: each title is read from its provider again.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/metadata")]
    public sealed class RefreshWorksMetadataV1 : AdminService<WorksBulkParametersV1, HeavyWorksDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;

        internal RefreshWorksMetadataV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWorkBulkTargetsAsync(new WorkTargetChoiceData(Data.WorkIds, null, null), ct), CancellationToken);
            var outcomes = new List<WorkOutcomeData>();
            foreach (var id in Data.WorkIds)
            {
                var target = targets.FirstOrDefault(item => item.Id == id);
                if (target is null)
                {
                    outcomes.Add(new WorkOutcomeData(id, "#" + id, "not_found"));
                    continue;
                }

                outcomes.Add(await WorkBulkOutcomes.TryAsync(id, target.CanonicalTitle, async () =>
                {
                    await RefreshWorkMetadataV1.RefreshAsync(Context, _metadata, _books, _aniList, _music, _games, id, CancellationToken);
                    return "done";
                }));
            }

            return WorkBulkOutcomes.Of(outcomes);
        }
    }

    // Delete like Sonarr for several titles: files too when asked, an exclusion when asked; a title that cannot be deleted stays and says why.
    [ServiceEndpoint("POST", "/api/v1/admin/works/bulk/deletion")]
    public sealed class DeleteWorksV1 : AdminService<WorksBulkParametersV1, DeleteWorksDataV1>
    {
        private readonly LibraryReplacements _replacements;
        private readonly CoverStore _covers;

        internal DeleteWorksV1(LibraryReplacements replacements, CoverStore covers)
        {
            _replacements = replacements;
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWorkBulkTargetsAsync(new WorkTargetChoiceData(Data.WorkIds, null, null), ct), CancellationToken);
            var outcomes = new List<WorkOutcomeData>();
            foreach (var id in Data.WorkIds)
            {
                var target = targets.FirstOrDefault(item => item.Id == id);
                if (target is null)
                {
                    outcomes.Add(new WorkOutcomeData(id, "#" + id, "not_found"));
                    continue;
                }

                outcomes.Add(await WorkBulkOutcomes.TryAsync(id, target.CanonicalTitle, async () =>
                {
                    await DeleteWorkV1.DeleteAsync(Context, _replacements, _covers, id, Data.DeletesFiles, Data.AddsExclusion, Auth.AccountId!.Value, CancellationToken);
                    return "done";
                }));
            }

            return WorkBulkOutcomes.Of(outcomes);
        }
    }
}
