using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/files")]
    public sealed class ReadLibraryFilesV1 : AdminService<ReadLibraryFilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryFileDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryFilesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryFilesParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadLibraryFilesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Subtitles follow their media file.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    root."Name" AS "LibraryRootName",
                    root."MediaTypeId",
                    file."RelativePath",
                    file."SizeBytes",
                    file."MissingSince",
                    file."MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "WorkTitle",
                    (SELECT rom."Name" FROM "GameRoms" AS rom WHERE rom."Sha1" = file."Sha1" ORDER BY rom."GameRomCatalogId" LIMIT 1) AS "RomName",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssetSiblings" AS sibling
                            JOIN "StoredFiles" AS present ON present."MediaAssetId" = sibling."SiblingMediaAssetId" AND present."MissingSince" IS NULL AND NOT present."IsSubtitle"
                        WHERE
                            sibling."MediaAssetId" = asset."Id"
                    ) AS "HasOtherVersions",
                    coalesce(asset."IsProtected", FALSE) AS "IsProtected"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    (@LibraryRootId::bigint IS NULL OR file."LibraryRootId" = @LibraryRootId)
                    AND (NOT @OnlyUnmatched OR file."MediaAssetId" IS NULL)
                    AND NOT file."IsSubtitle"
                ORDER BY
                    file."LibraryRootId",
                    file."RelativePath"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new LibraryFileFilterData(Parameters.LibraryRootId, Parameters.OnlyUnmatched));
            sql.Page = Page;
            var rows = await Context.ReadSql.ReadPageAsync<LibraryFileRowData>(sql, CancellationToken);

            // Suggest identity from the verified dump, else from the path.
            var paths = rows.Items.ToDictionary(row => row.Id, row => row.RomName is { } rom
                ? new MediaPathInfo(GameRomMatching.Title(rom), null, null, null, null)
                : MediaPathParser.Parse(row.RelativePath, row.MediaTypeId));
            var titles = paths.Values.Select(path => Limit(path.Title).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    lower(title."Title") AS "Title",
                    work."MediaTypeId",
                    min(work."Id") AS "WorkId"
                FROM
                    "WorkTitles" AS title
                    JOIN "Works" AS work ON work."Id" = title."WorkId"
                WHERE
                    lower(title."Title") = ANY(@Titles)
                GROUP BY
                    lower(title."Title"),
                    work."MediaTypeId"
                HAVING
                    count(DISTINCT work."Id") = 1;
                """;
            sql.Parameters.Add(new WorkTitleLookupData(titles));
            var matches = titles.Length == 0 ? [] : await Context.ReadSql.ReadListAsync<WorkTitleMatchData>(sql, CancellationToken);

            var items = rows.Items.Select(row =>
            {
                var path = paths[row.Id];
                var title = Limit(path.Title);
                var match = matches.FirstOrDefault(item => item.MediaTypeId == row.MediaTypeId && item.Title == title.ToLowerInvariant());
                return new LibraryFileDetailsV1(row.Id, row.LibraryRootId, row.LibraryRootName, row.MediaTypeId, row.RelativePath, row.SizeBytes, row.MissingSince, row.MediaAssetId,
                    row.WorkId, row.WorkTitle, title, match?.WorkId, path.WorkUnitTypeId, path.UnitNumber, path.ParentUnitNumber, row.HasOtherVersions, row.IsProtected);
            }).ToArray();
            return new PageResult<LibraryFileDetailsV1>(items, Page);
        }

        private static string Limit(string title) => title.Length > 500 ? title[..500] : title;
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/files/{storedFileId:long}/assignment")]
    public sealed class AssignLibraryFileV1 : AdminService<StoredFileParametersV1, AssignLibraryFileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AssignLibraryFileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var data = new StoredFileAssignmentData(Parameters.StoredFileId, Data.WorkId, Data.Title, Data.WorkUnitTypeId, Data.UnitNumber, Data.ParentUnitNumber, Data.GamePlatformId,
                true);
            var asset = await Context.AssignStoredFileAsync(data, CancellationToken);
            await Context.RecordActivityAsync(new ActivityEventData(ActivityEventType.ManualImport, storedFileId: Parameters.StoredFileId, accountId: Auth.AccountId), CancellationToken);
            return new AssignLibraryFileResultV1(asset.Id);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/library/files/{storedFileId:long}/assignment")]
    public sealed class UnassignLibraryFileV1 : AdminService<StoredFileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.UnassignStoredFileAsync(new StoredFileParameters(Parameters.StoredFileId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/roots/{libraryRootId:long}/scans")]
    public sealed class QueueLibraryScanV1 : AdminService<LibraryRootParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<QueueLibraryScanResultV1>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRootParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryRootParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            // The background worker performs storage I/O.
            var data = new OperationCreationData(OperationKind.LibraryScan, Parameters.LibraryRootId, Auth.AccountId);
            return new QueueLibraryScanResultV1(await Context.QueueLibraryScanAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/library/renames")]
    public sealed class ReadLibraryRenamesV1 : AdminService<ReadLibraryRenamesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LibraryRenamePlanV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryRenamesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryRenamesParametersV1 parameters, Type resultType) => new("storage.read");
        protected override PageRequest GetPageRequest(ReadLibraryRenamesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // The whole plan is fingerprinted; one page is shown.
            var renames = LibraryRenameLogic.Plan(await Context.ReadLibraryRenameFilesAsync(CancellationToken), await Context.ReadNamingPatternsAsync(CancellationToken));
            var items = renames.Skip((Page.Page - 1) * Page.PageSize).Take(Page.PageSize)
                .Select(rename => new LibraryRenameV1(rename.StoredFileId, rename.LibraryRootName, rename.RelativePath, rename.TargetPath));
            return new LibraryRenamePlanV1(new BaseDataList<LibraryRenameV1>(items), LibraryRenameLogic.Fingerprint(renames));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/renames")]
    public sealed class ApplyLibraryRenamesV1 : AdminService<LibraryRenamesParametersV1, ApplyLibraryRenamesDataV1>
    {
        private readonly StorageAccess _storage;

        internal ApplyLibraryRenamesV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ApplyLibraryRenamesResultV1>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRenamesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryRenamesParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (renames, roots) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var plan = LibraryRenameLogic.Plan(await ctx.ReadLibraryRenameFilesAsync(ct), await ctx.ReadNamingPatternsAsync(ct));
                var locations = new List<LibraryRootLocationData>();
                foreach (var rootId in plan.Select(rename => rename.LibraryRootId).Distinct())
                {
                    if (await ctx.ReadLibraryRootLocationAsync(new LibraryRootParameters(rootId), ct) is { } location)
                    {
                        locations.Add(location);
                    }
                }

                return (plan, locations);
            }, CancellationToken);
            if (LibraryRenameLogic.Fingerprint(renames) != Data.Fingerprint)
            {
                throw new DataValidationException(nameof(ApplyLibraryRenamesDataV1.Fingerprint), "library_rename_changed");
            }

            var renamed = 0;
            var folders = new List<(StorageFiles Files, string Root, string Folder)>();
            foreach (var root in roots)
            {
                if (await OpenAsync(root) is not { } files)
                {
                    continue;
                }

                // The media file moves first; its subtitles only follow it.
                foreach (var group in renames.Where(rename => rename.LibraryRootId == root.Id).GroupBy(rename => rename.MediaStoredFileId))
                {
                    foreach (var rename in group)
                    {
                        if (!await MoveAsync(files, root.RelativePath, rename, folders))
                        {
                            break;
                        }

                        renamed++;
                    }
                }
            }

            // Folders a rename emptied or created in vain go, never the library root.
            foreach (var (files, root, folder) in folders.OrderByDescending(item => item.Folder.Length))
            {
                try
                {
                    await files.DeleteEmptyFoldersAsync(folder, root, CancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
                {
                    // Left for the next rename.
                }
            }

            return new ApplyLibraryRenamesResultV1(renamed, renames.Count - renamed);
        }

        // The library's connection; null while it cannot be reached.
        private async Task<StorageFiles?> OpenAsync(LibraryRootLocationData root)
        {
            try
            {
                var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(root.StorageMountId, ct), CancellationToken);
                var files = _storage.Open(mount);
                return await files.GetAsync(root.RelativePath, CancellationToken) is { IsDirectory: true } ? files : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return null;
            }
        }

        // Move first, then record; a file whose record fails moves back.
        private async Task<bool> MoveAsync(StorageFiles files, string root, LibraryRenameData rename, List<(StorageFiles Files, string Root, string Folder)> folders)
        {
            var source = StoragePath.Combine(root, rename.RelativePath);
            var destination = StoragePath.Combine(root, rename.TargetPath);
            try
            {
                folders.Add((files, root, StoragePath.Parent(source)));
                folders.Add((files, root, StoragePath.Parent(destination)));
                if (!await files.MoveAsync(source, destination, CancellationToken))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return false;
            }

            // A moved file is recorded even when the request ends meanwhile.
            bool recorded;
            try
            {
                recorded = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    // The history names the media file's old place; its subtitles follow it silently.
                    var renamed = await ctx.RenameStoredFileAsync(rename, ct);
                    if (renamed && rename.StoredFileId == rename.MediaStoredFileId)
                    {
                        await ctx.RecordActivityAsync(new ActivityEventData(ActivityEventType.FileRenamed, storedFileId: rename.StoredFileId, accountId: Auth.AccountId,
                            previousPath: rename.RelativePath), ct);
                    }

                    return renamed;
                }, CancellationToken.None);
            }
            catch
            {
                await files.MoveAsync(destination, source, CancellationToken.None);
                throw;
            }

            if (!recorded)
            {
                await files.MoveAsync(destination, source, CancellationToken.None);
            }

            return recorded;
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/library/naming")]
    public sealed class ReadNamingPatternsV1 : AdminService<ReadNamingPatternsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<NamingPatternV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadNamingPatternsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadNamingPatternsParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pattern."MediaTypeId",
                    pattern."Pattern",
                    pattern."Version"
                FROM
                    "NamingPatterns" AS pattern
                ORDER BY
                    pattern."MediaTypeId";
                """;
            return new BaseDataList<NamingPatternV1>([.. (await Context.ReadSql.ReadListAsync<NamingPatternSaveData>(sql, CancellationToken)).Select(row =>
            {
                var (folder, file) = NamingPattern.Split(row.Pattern);
                return new NamingPatternV1(row.MediaTypeId, folder, file, NamingPattern.Example(row.MediaTypeId, row.Pattern), row.Version);
            })]);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/naming/{mediaTypeId}")]
    public sealed class UpdateNamingPatternV1 : AdminService<NamingPatternParametersV1, UpdateNamingPatternDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(NamingPatternParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(NamingPatternParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveNamingPatternAsync(new NamingPatternSaveData(Parameters.MediaTypeId, NamingPattern.Join(Data.Folder, Data.File), Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
