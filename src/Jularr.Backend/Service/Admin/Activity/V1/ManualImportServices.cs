using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    // What a manual import needs of its film or series.
    internal sealed record ManualImportTarget(WantedTargetData Target, NamingSet Patterns, IReadOnlyList<TargetUnit> Episodes, LibraryRootLocationData? Root);

    internal static class ManualImport
    {
        public static async Task<ManualImportTarget> ReadTargetAsync(LogicContext context, long wantedItemId, CancellationToken ct)
        {
            var (target, patterns, units, root) = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var wanted = await ctx.ReadWantedTargetAsync(wantedItemId, token);
                return (wanted, await ctx.ReadNamingPatternsAsync(token), wanted.MediaTypeId == MediaType.Series ? await ctx.ReadUnitStatesAsync(wantedItemId, token) : [],
                    await ctx.ReadImportRootAsync(wantedItemId, token));
            }, ct);

            // Films and episodes only; the other media types have their own imports.
            return target.MediaTypeId is MediaType.Movie or MediaType.Series ? new ManualImportTarget(target, patterns, EpisodeNumbering.Episodes(units), root)
                : throw new KeyNotFoundException("Only a film or a series can be imported by hand.");
        }

        // Every file of the chosen folder that a film or episode import can use.
        public static async Task<(StorageFiles Source, IReadOnlyList<StorageFile> Files, bool IsReadOnly)> ReadFolderAsync(LogicContext context, StorageAccess storage, WantedTargetData target,
            long storageMountId, string relativePath, CancellationToken ct)
        {
            var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(storageMountId, token), ct);
            var source = storage.Open(mount);
            if (await source.GetAsync(relativePath, ct) is not { IsDirectory: true })
            {
                throw new DataValidationException(nameof(ReadManualImportDataV1.RelativePath), "unknown_reference");
            }

            var found = new List<StorageFile>();
            await foreach (var file in source.EnumerateFilesAsync(relativePath, LibraryFileTypes.GetImportExtensions(target.MediaTypeId, target.MediaSubtypeId), new StorageScanState(), ct))
            {
                found.Add(file);
            }

            return (source, found, mount.IsReadOnly);
        }
    }

    // The films and series a folder can be imported to.
    [ServiceEndpoint("GET", "/api/v1/admin/activity/manual-import/works")]
    public sealed class FindManualImportWorksV1 : AdminService<ManualImportWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ManualImportWorkV1>>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportWorksParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportWorksParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    work."MediaTypeId" IN (1, 2)
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkTitles" AS title
                        WHERE
                            title."WorkId" = work."Id"
                            AND title."Title" ILIKE '%' || replace(replace(replace(@Query, '\', '\\'), '%', '\%'), '_', '\_') || '%'
                    )
                ORDER BY
                    work."CanonicalTitle",
                    wanted."Id"
                LIMIT 20;
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<ManualImportWorkV1>(await Context.ReadSql.ReadListAsync<ManualImportWorkV1>(sql, CancellationToken));
        }
    }

    // The film or series a manual import is for.
    [ServiceEndpoint("GET", "/api/v1/admin/activity/manual-import/{wantedItemId:long}")]
    public sealed class ReadManualImportWorkV1 : AdminService<ManualImportParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportWorkV1>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND work."MediaTypeId" IN (1, 2);
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadOptionalAsync<ManualImportWorkV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("Only a film or a series can be imported by hand.");
        }
    }

    // The videos of a folder with what their names say and the names they would get.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/{wantedItemId:long}/files")]
    public sealed class ReadManualImportFilesV1 : AdminService<ManualImportParametersV1, ReadManualImportDataV1>
    {
        private readonly StorageAccess _storage;

        internal ReadManualImportFilesV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportFolderV1>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var loaded = await ManualImport.ReadTargetAsync(Context, Parameters.WantedItemId, CancellationToken);
            var target = loaded.Target;
            var (_, files, _) = await ManualImport.ReadFolderAsync(Context, _storage, target, Data.StorageMountId, Data.RelativePath, CancellationToken);
            return new ManualImportFolderV1(new BaseDataList<ManualImportFileV1>(ManualImportLogic.Propose(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, files,
                loaded.Patterns, loaded.Episodes).Select(proposal => new ManualImportFileV1(proposal.Path, proposal.SizeBytes, proposal.Season, proposal.Episode, proposal.QualityKey, proposal.TargetName))));
        }
    }

    // Puts the chosen files of a folder into the library as the film or the episodes their owner says they are.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/{wantedItemId:long}/import")]
    public sealed class RunManualImportV1 : AdminService<ManualImportParametersV1, RunManualImportDataV1>
    {
        private readonly StorageAccess _storage;
        private readonly DownloadImport _import;

        internal RunManualImportV1(StorageAccess storage, DownloadImport import)
        {
            _storage = storage;
            _import = import;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var loaded = await ManualImport.ReadTargetAsync(Context, Parameters.WantedItemId, CancellationToken);
            var target = loaded.Target;
            var (source, files, isReadOnly) = await ManualImport.ReadFolderAsync(Context, _storage, target, Data.StorageMountId, Data.RelativePath, CancellationToken);

            // A move deletes from the source, which a read-only connection does not allow.
            if (Data.ImportTransferModeId == ImportTransferMode.Move && isReadOnly)
            {
                throw new DataValidationException(nameof(Data.ImportTransferModeId), "invalid_transition");
            }

            var root = loaded.Root ?? throw new ServiceUnavailableException("import_no_library");

            var chosen = Data.Files.Where(choice => choice.Imports).ToArray();
            var placements = ImportPlacementLogic.PlanChosen(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, files,
                [.. chosen.Select(choice => (choice.Path, choice.Season, choice.Episode))], loaded.Patterns, loaded.Episodes);
            if (chosen.Length == 0 || placements.Count(placement => placement.IsMedia) != chosen.Length)
            {
                throw new DataValidationException(nameof(Data.Files), "unknown_reference");
            }

            var isMovie = target.MediaTypeId == MediaType.Movie;
            var commit = await _import.CommitAsync(Context, new ImportCommitRequest(root, target.WorkId, null, placements, source, Data.RelativePath, files,
                path => ReleaseFacts.OfPath(path, isMovie), Data.ImportTransferModeId, null, Auth.AccountId, target.MediaTypeId,
                target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target)), CancellationToken);
            return commit.FailureKey is { } failure ? throw new ServiceUnavailableException(failure) : new ManualImportResultV1(commit.Assets.Count);
        }
    }
}
