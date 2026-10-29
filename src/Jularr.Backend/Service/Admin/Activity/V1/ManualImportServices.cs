using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
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
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    // What a manual import needs of its film or series.
    internal sealed record ManualImportTarget(WantedTargetData Target, NamingSet Patterns, IReadOnlyList<TargetUnit> Episodes, LibraryRootLocationData? Root);

    // The waiting download an import by hand is for: the files already imported, whether the person finishes it now, and the proposal it keeps them in.
    internal sealed record ManualImportDownload(long OperationId, IReadOnlyList<string> Handled, bool Finishes, string Proposal);

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
            return (source, await ListFolderAsync(source, target, relativePath, ct), mount.IsReadOnly);
        }

        public static async Task<IReadOnlyList<StorageFile>> ListFolderAsync(StorageFiles source, WantedTargetData target, string relativePath, CancellationToken ct)
        {
            if (await source.GetAsync(relativePath, ct) is not { IsDirectory: true })
            {
                throw new DataValidationException(nameof(ReadManualImportDataV1.RelativePath), "unknown_reference");
            }

            var found = new List<StorageFile>();
            await foreach (var file in source.EnumerateFilesAsync(relativePath, LibraryFileTypes.GetImportExtensions(target.MediaTypeId, target.MediaSubtypeId), new StorageScanState(), ct))
            {
                found.Add(file);
            }

            return found;
        }

        // The waiting download and the folder its finished files lie in, on a storage connection or on the server itself.
        public static async Task<(ManualImportDownloadData Download, ImportSourceRef Reference, StorageFiles Source, bool IsReadOnly)> OpenDownloadAsync(LogicContext context, StorageAccess storage,
            long operationId, CancellationToken ct)
        {
            var download = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadManualImportDownloadAsync(operationId, token), ct)
                ?? throw new KeyNotFoundException("The download does not wait for a person.");
            var reference = ImportProposal.ReadSource(download.AttentionProposal) ?? throw new KeyNotFoundException("The download did not keep where its files are.");
            if (reference.StorageMountId is { } mountId)
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
                return (download, reference, storage.Open(mount), mount.IsReadOnly);
            }

            return (download, reference, new LocalStorageFiles(reference.LocalRoot ?? throw new KeyNotFoundException("The download did not keep where its files are.")), false);
        }

        // The film or series of every choice: the one the import is for and the others a person gave a file.
        public static async Task<Dictionary<long, ManualImportTarget>> ReadTargetsAsync(LogicContext context, long defaultWantedItemId, IEnumerable<ManualImportChoiceV1> choices, CancellationToken ct)
        {
            var targets = new Dictionary<long, ManualImportTarget>();
            foreach (var id in choices.Select(choice => choice.WantedItemId ?? defaultWantedItemId).Append(defaultWantedItemId).Distinct())
            {
                targets[id] = await ReadTargetAsync(context, id, ct);
            }

            return targets;
        }

        // The qualities a person gave, each one a quality its film or series can have.
        public static Dictionary<string, string> ReadQualities(IEnumerable<ManualImportChoiceV1> choices, WantedTargetData target)
        {
            var qualities = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var choice in choices.Where(choice => choice.QualityKey is not null))
            {
                if (!ProfileQualities.For(target.MediaTypeId, target.MediaSubtypeId).Contains(choice.QualityKey!))
                {
                    throw new DataValidationException(nameof(ManualImportChoiceV1.QualityKey), "invalid_value");
                }

                qualities[choice.Path] = choice.QualityKey!;
            }

            return qualities;
        }

        // The languages a person chose, by file, each one the language catalog knows.
        public static Dictionary<string, ChosenLanguages> ReadLanguages(IEnumerable<ManualImportChoiceV1> choices)
        {
            var languages = new Dictionary<string, ChosenLanguages>(StringComparer.Ordinal);
            foreach (var choice in choices)
            {
                if (FileLanguageChoice.Read(nameof(ManualImportChoiceV1.AudioLanguages), choice.AudioLanguages, choice.SubtitleLanguages) is { } chosen)
                {
                    languages[choice.Path] = chosen;
                }
            }

            return languages;
        }

        // The numbers of a series' files, the way every plan takes them; a film needs none.
        public static (string Path, int? Season, int? Episode)[] Numbers(IEnumerable<ManualImportChoiceV1> choices) => [.. choices.Select(choice => (choice.Path, choice.Season, choice.Episode))];

        // Places the chosen files, each as the film or episode its owner says, grouped by the film or series they go to; the files of one waiting download may go to several. What a person must settle
        // comes back as an unavailable service when nothing was placed, a mistake in the choice as invalid data; when some files were placed, each file's outcome says what became of it. A waiting download
        // leaves the queue once every video of it is imported or the person finishes it; until then it keeps the files already imported.
        public static async Task<ManualImportResultV1> RunAsync(LogicContext context, DownloadImport import, long wantedItemId, StorageFiles source, string directory,
            IReadOnlyList<StorageFile> files, bool isReadOnly, ImportTransferMode mode, BaseDataList<ManualImportChoiceV1> choices, long? accountId, ManualImportDownload? download, CancellationToken ct)
        {
            var chosen = choices.Where(choice => choice.Imports).ToArray();

            // A download may be finished without a further file once a person imported some of it.
            var finishOnly = chosen.Length == 0 && download is { Finishes: true, Handled.Count: > 0 };
            if (!finishOnly && (chosen.Length == 0 || chosen.Select(choice => choice.Path).Distinct(StringComparer.Ordinal).Count() != chosen.Length))
            {
                throw new DataValidationException(nameof(RunManualImportDataV1.Files), "unknown_reference");
            }

            // A move deletes from the source, which a read-only connection does not allow.
            if (mode == ImportTransferMode.Move && isReadOnly && !finishOnly)
            {
                throw new DataValidationException(nameof(RunManualImportDataV1.ImportTransferModeId), "invalid_transition");
            }

            // Plan every group before anything is placed.
            var targets = await ReadTargetsAsync(context, wantedItemId, chosen, ct);
            var plans = new List<(ManualImportTarget Loaded, ManualImportChoiceV1[] Choices, IReadOnlyList<ImportPlacementData> Placements, Dictionary<string, string> Qualities,
                Dictionary<string, ChosenLanguages> Languages)>();
            foreach (var group in chosen.GroupBy(choice => choice.WantedItemId ?? wantedItemId))
            {
                var loaded = targets[group.Key];
                var target = loaded.Target;
                var members = group.ToArray();
                var qualities = ReadQualities(members, target);
                var placements = ImportPlacementLogic.PlanChosen(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, files, Numbers(members), loaded.Patterns, loaded.Episodes,
                    qualities, ImportPlacementLogic.KnownValues(target));
                if (placements.Count(placement => placement.IsMedia) != members.Length)
                {
                    throw new DataValidationException(nameof(RunManualImportDataV1.Files), "unknown_reference");
                }

                plans.Add((loaded, members, placements, qualities, ReadLanguages(members)));
            }

            var outcomes = new List<ManualImportOutcomeV1>();
            var handled = new List<string>(download?.Handled ?? []);
            var imported = 0;
            var finished = false;
            string? firstFailure = null;
            var finishes = download is not null && ManualImportLogic.CompletesDownload(files, handled.Concat(chosen.Select(choice => choice.Path)), download.Finishes);
            for (var index = 0; index < plans.Count; index++)
            {
                var (loaded, members, placements, qualities, languages) = plans[index];
                var target = loaded.Target;
                var isMovie = target.MediaTypeId == MediaType.Movie;

                // Only the last group finishes the download, and only when nothing before it went wrong.
                var finishesHere = finishes && index == plans.Count - 1 && firstFailure is null;
                var commit = loaded.Root is { } root
                    ? await import.CommitAsync(context, new ImportCommitRequest(root, target.WorkId, null, placements, source, directory, files,
                        path => ReleaseFacts.WithQuality(ReleaseFacts.OfPath(path, isMovie), path, qualities.GetValueOrDefault(path)), mode, download?.OperationId, accountId, target.MediaTypeId,
                        target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target), languages, finishesHere), ct)
                    : new ImportCommit("import_no_library", []);
                firstFailure ??= commit.FailureKey;
                if (commit.FailureKey is null)
                {
                    imported += commit.Assets.Count;
                    handled.AddRange(members.Select(member => member.Path));
                    finished |= finishesHere;
                }

                outcomes.AddRange(members.Select(member => new ManualImportOutcomeV1(member.Path, target.CanonicalTitle, commit.FailureKey, languages.GetValueOrDefault(member.Path)?.Audio,
                    languages.GetValueOrDefault(member.Path)?.Subtitles)));
            }

            if (imported == 0 && firstFailure is not null)
            {
                throw new ServiceUnavailableException(firstFailure);
            }

            if (download is not null && finishOnly)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.CompleteDownloadAsync(new DownloadOperationParameters(download.OperationId), token);
                    return true;
                }, ct);
                finished = true;
            }
            else if (download is not null && !finished && imported > 0)
            {
                // The download keeps waiting, and remembers what was imported.
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.SaveManualImportProgressAsync(new ManualImportProgressData(download.OperationId, ImportProposal.WithHandled(download.Proposal, handled)), token);
                    return true;
                }, ct);
            }

            return new ManualImportResultV1(imported, new BaseDataList<ManualImportOutcomeV1>(outcomes), finished);
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
                    (@AnyMediaType OR work."MediaTypeId" IN (1, 2))
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
                loaded.Patterns, loaded.Episodes, ImportPlacementLogic.KnownValues(target)).Select(proposal => new ManualImportFileV1(proposal.Path, proposal.SizeBytes, proposal.Season, proposal.Episode, proposal.QualityKey, proposal.TargetName, proposal.Languages))));
        }
    }

    // The names the library would give the chosen files as their owners decided, and the film or series each one goes to.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/{wantedItemId:long}/names")]
    public sealed class PreviewManualImportV1 : AdminService<ManualImportParametersV1, PreviewManualImportDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ManualImportNameV1>>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await ManualImport.ReadTargetsAsync(Context, Parameters.WantedItemId, Data.Files, CancellationToken);
            var names = new List<ManualImportNameV1>();
            foreach (var group in Data.Files.GroupBy(choice => choice.WantedItemId ?? Parameters.WantedItemId))
            {
                var loaded = targets[group.Key];
                var target = loaded.Target;
                var members = group.ToArray();
                var named = ManualImportLogic.NameChosen(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, ManualImport.Numbers(members),
                    ManualImport.ReadQualities(members, target), loaded.Patterns, loaded.Episodes, ImportPlacementLogic.KnownValues(target));
                ManualImport.ReadLanguages(members);
                names.AddRange(members.Select(member => new ManualImportNameV1(member.Path, group.Key, target.MediaTypeId, target.CanonicalTitle, named.GetValueOrDefault(member.Path))));
            }

            return new BaseDataList<ManualImportNameV1>(names);
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
            var (source, files, isReadOnly) = await ManualImport.ReadFolderAsync(Context, _storage, loaded.Target, Data.StorageMountId, Data.RelativePath, CancellationToken);
            return await ManualImport.RunAsync(Context, _import, Parameters.WantedItemId, source, Data.RelativePath, files, isReadOnly, Data.ImportTransferModeId, Data.Files, Auth.AccountId, null, CancellationToken);
        }
    }
    // The finished files of a download that waits for a person.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/manual-import/files")]
    public sealed class ReadManualImportDownloadV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
        private readonly StorageAccess _storage;

        internal ReadManualImportDownloadV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportDownloadV1>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (download, reference, source, _) = await ManualImport.OpenDownloadAsync(Context, _storage, Parameters.OperationId, CancellationToken);
            var loaded = await ManualImport.ReadTargetAsync(Context, download.WantedItemId, CancellationToken);
            var target = loaded.Target;
            var files = await ManualImport.ListFolderAsync(source, target, reference.Directory, CancellationToken);
            var handled = ImportProposal.ReadHandled(download.AttentionProposal).ToHashSet(StringComparer.Ordinal);
            return new ManualImportDownloadV1(download.WantedItemId, new BaseDataList<ManualImportFileV1>(ManualImportLogic.Propose(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle,
                target.ReleaseYear, files, loaded.Patterns, loaded.Episodes, ImportPlacementLogic.KnownValues(target)).Select(proposal => new ManualImportFileV1(proposal.Path, proposal.SizeBytes, proposal.Season, proposal.Episode,
                    proposal.QualityKey, proposal.TargetName, proposal.Languages, handled.Contains(proposal.Path)))));
        }
    }

    // Finishing a manual download does not mean every source file was selected.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/manual-import")]
    public sealed class RunManualImportDownloadV1 : AdminService<ActivityOperationParametersV1, RunManualImportDownloadDataV1>
    {
        private readonly StorageAccess _storage;
        private readonly DownloadImport _import;

        internal RunManualImportDownloadV1(StorageAccess storage, DownloadImport import)
        {
            _storage = storage;
            _import = import;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (download, reference, source, isReadOnly) = await ManualImport.OpenDownloadAsync(Context, _storage, Parameters.OperationId, CancellationToken);
            var loaded = await ManualImport.ReadTargetAsync(Context, download.WantedItemId, CancellationToken);
            var files = await ManualImport.ListFolderAsync(source, loaded.Target, reference.Directory, CancellationToken);
            var handled = ImportProposal.ReadHandled(download.AttentionProposal);
            // Include non-media files when deciding whether completed source folders can be removed.
            // A failed or incomplete scan must not make the import itself fail.
            var allFiles = new List<StorageFile>();
            var sourceScanComplete = false;
            try
            {
                var scan = new StorageScanState();
                await foreach (var file in source.EnumerateFilesAsync(reference.Directory, null, scan, CancellationToken))
                    allFiles.Add(file);
                sourceScanComplete = scan.IsComplete;
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // Leave the source untouched: there is no proof the folder was fully imported.
            }

            var result = await ManualImport.RunAsync(Context, _import, download.WantedItemId, source, reference.Directory, files, isReadOnly, Data.ImportTransferModeId, Data.Files, Auth.AccountId,
                new ManualImportDownload(download.OperationId, handled, Data.FinishesDownload, download.AttentionProposal), CancellationToken);
            if (result.FinishedDownload && sourceScanComplete
                && ImportPlacementLogic.AllSourceFilesSelected(allFiles.Select(file => file.RelativePath),
                    handled.Concat(result.Files.Where(file => file.FailureKey is null).Select(file => file.Path))))
            {
                await _import.RemoveFromClientAsync(Context, download.AcquisitionConnectionId, download.ExternalId,
                    DownloadSourceInventory.Fingerprint(allFiles), CancellationToken);
            }

            // A finished manual import with omitted files remains in the client for explicit cleanup.
            return result;
        }
    }
}
