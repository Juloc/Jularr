using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using System.Globalization;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    // What a manual import needs of its film, series or book: the episodes of a series, the volumes of a light novel or manga and a manga's chapters with their volume.
    internal sealed record ManualImportTarget(WantedTargetData Target, NamingSet Patterns, IReadOnlyList<TargetUnit> Units, LibraryRootLocationData? Root, IReadOnlyList<TargetUnit> Chapters);

    // The waiting download an import by hand is for: the files already imported, whether the person finishes it now, and the proposal it keeps them in.
    internal sealed record ManualImportDownload(long OperationId, IReadOnlyList<string> Handled, bool Finishes, string Proposal, string ReleaseTitle);

    internal static class ManualImport
    {
        public static async Task<ManualImportTarget> ReadTargetAsync(LogicContext context, long wantedItemId, CancellationToken ct)
        {
            var (target, patterns, units, chapters, root) = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var wanted = await ctx.ReadWantedTargetAsync(wantedItemId, token);
                var units = wanted.MediaTypeId == MediaType.Series ? await ctx.ReadUnitStatesAsync(wantedItemId, token)
                    : wanted.MediaSubtypeId is MediaSubtype.LightNovel or MediaSubtype.Manga ? await ctx.ReadBookUnitStatesAsync(wantedItemId, WorkUnitType.Volume, token) : [];
                var chapters = wanted.MediaSubtypeId == MediaSubtype.Manga ? await ctx.ReadBookUnitStatesAsync(wantedItemId, WorkUnitType.Chapter, token) : [];
                return (wanted, await ctx.ReadNamingPatternsAsync(token), units, chapters, await ctx.ReadImportRootAsync(wantedItemId, token));
            }, ct);

            // Films, episodes and books; music and games have their own imports.
            return target.MediaTypeId switch
            {
                MediaType.Movie or MediaType.Series => new ManualImportTarget(target, patterns, EpisodeNumbering.Episodes(units), root, []),
                MediaType.Book => new ManualImportTarget(target, patterns, [.. units.Select(unit => new TargetUnit(unit.WorkUnitId, null, unit.Number, unit.HasMedia, title: unit.Title))], root,
                    [.. chapters.Select(unit => new TargetUnit(unit.WorkUnitId, unit.ParentNumber, unit.Number, unit.HasMedia, title: unit.Title))]),
                _ => throw new KeyNotFoundException("Only a film, a series or a book can be imported by hand.")
            };
        }

        // Every file of any kind an import takes (videos with their subtitles, ebooks and comics), so one folder can hold films, episodes and books.
        private static readonly IReadOnlySet<string> AllImportExtensions = new HashSet<string>(new[]
            {
                LibraryFileTypes.GetImportExtensions(MediaType.Movie, null), LibraryFileTypes.GetImportExtensions(MediaType.Series, null), LibraryFileTypes.GetImportExtensions(MediaType.Book, null),
                LibraryFileTypes.GetImportExtensions(MediaType.Book, MediaSubtype.Manga), LibraryFileTypes.GetImportExtensions(MediaType.Book, MediaSubtype.LightNovel)
            }.SelectMany(extensions => extensions ?? new HashSet<string>()), StringComparer.OrdinalIgnoreCase);

        // The chosen folder of a storage connection with every file an import can use, and whether files may be moved out of it.
        public static async Task<(StorageFiles Source, IReadOnlyList<StorageFile> Files, bool IsReadOnly)> ReadFolderAsync(LogicContext context, StorageAccess storage, long storageMountId, string relativePath,
            CancellationToken ct)
        {
            var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(storageMountId, token), ct);
            var source = storage.Open(mount);
            return (source, await ListFolderAsync(source, relativePath, ct), mount.IsReadOnly);
        }

        public static async Task<IReadOnlyList<StorageFile>> ListFolderAsync(StorageFiles source, string relativePath, CancellationToken ct)
        {
            if (await source.GetAsync(relativePath, ct) is not { IsDirectory: true })
            {
                throw new DataValidationException(nameof(ReadManualImportDataV1.RelativePath), "unknown_reference");
            }

            var found = new List<StorageFile>();
            await foreach (var file in source.EnumerateFilesAsync(relativePath, AllImportExtensions, new StorageScanState(), ct))
            {
                found.Add(file);
            }

            return found;
        }

        // The waiting download, the folder its finished files lie in (on a storage connection or on the server itself) and how that folder is shown.
        public static async Task<(ManualImportDownloadData Download, ImportSourceRef Reference, StorageFiles Source, bool IsReadOnly, string Folder)> OpenDownloadAsync(LogicContext context,
            StorageAccess storage, long operationId, CancellationToken ct)
        {
            var download = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadManualImportDownloadAsync(operationId, token), ct)
                ?? throw new KeyNotFoundException("The download does not wait for a person.");
            var reference = ImportProposal.ReadSource(download.AttentionProposal) ?? throw new KeyNotFoundException("The download did not keep where its files are.");
            if (reference.StorageMountId is { } mountId)
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
                return (download, reference, storage.Open(mount), mount.IsReadOnly, mount.Name + " /" + reference.Directory);
            }

            var root = reference.LocalRoot ?? throw new KeyNotFoundException("The download did not keep where its files are.");
            return (download, reference, new LocalStorageFiles(root), false, reference.Directory.Length == 0 ? root : root.TrimEnd('/') + "/" + reference.Directory);
        }

        // What each video, ebook and comic of a folder is: the film, series or book of the library its names fit (a waiting download's own title for all of them), the episode, volume or
        // chapter, quality, languages and release group, the name the library would give it, and what a person must check. Handled files of a download and why its import left a file out
        // come along.
        public static async Task<IReadOnlyList<ManualImportFileV1>> DetectAsync(LogicContext context, IReadOnlyList<StorageFile> files, string folderName, long? wantedItemId,
            IReadOnlySet<string> handled, IReadOnlyDictionary<string, string> reasons, CancellationToken ct)
        {
            var media = ManualImportLogic.Importable(files);
            var recognised = new Dictionary<string, ManualImportRecognition>(StringComparer.Ordinal);
            if (wantedItemId is null && media.Length > 0)
            {
                var names = media.ToDictionary(file => file.RelativePath, file => ManualImportLogic.Names(file.RelativePath, folderName), StringComparer.Ordinal);
                var words = ManualImportLogic.SearchWords(media.SelectMany(file => names[file.RelativePath].Select(name => (name, ManualImportLogic.IsDocument(file.RelativePath)))));
                var candidates = words.Count == 0 ? [] : await ReadCandidatesAsync(context, words, ct);
                foreach (var file in media)
                {
                    recognised[file.RelativePath] = ManualImportLogic.Recognise(file.RelativePath, names[file.RelativePath], candidates);
                }
            }

            // Each title's files are proposed together, the way the import names them.
            var detected = new Dictionary<string, ManualImportFileV1>(StringComparer.Ordinal);
            foreach (var group in media.GroupBy(file => wantedItemId ?? recognised[file.RelativePath].Candidate?.WantedItemId))
            {
                if (group.Key is not { } id)
                {
                    foreach (var file in group)
                    {
                        var guess = ManualImportLogic.KindOf(file.RelativePath);
                        detected[file.RelativePath] = new ManualImportFileV1(file.RelativePath, file.SizeBytes, null, null, ManualImportLogic.QualityOf(guess, null, file.RelativePath), null,
                            guess == MediaType.Book ? BookLanguages(file.RelativePath) : ManualImportLogic.Languages(file.RelativePath), handled.Contains(file.RelativePath),
                            reasons.GetValueOrDefault(file.RelativePath), mediaTypeId: guess, releaseGroup: ManualImportLogic.ReleaseGroupOf(file.RelativePath, guess),
                            problemKey: recognised[file.RelativePath].ProblemKey);
                    }

                    continue;
                }

                var loaded = await ReadTargetAsync(context, id, ct);
                var target = loaded.Target;
                var title = target.CanonicalTitle + (target.ReleaseYear is { } year ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : "");

                // The film, an episode or a volume is its largest file of the folder; another file of the same is only a second one.
                var proposals = ManualImportLogic.Propose(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, [.. group], loaded.Patterns, loaded.Units,
                    ImportPlacementLogic.KnownValues(target), loaded.Chapters);
                var seconds = ManualImportLogic.Seconds(target.MediaTypeId, proposals);
                foreach (var proposal in proposals)
                {
                    detected[proposal.Path] = new ManualImportFileV1(proposal.Path, proposal.SizeBytes, proposal.Season, proposal.Episode, proposal.QualityKey, proposal.TargetName, proposal.Languages,
                        handled.Contains(proposal.Path), reasons.GetValueOrDefault(proposal.Path), id, target.MediaTypeId, target.MediaSubtypeId, title,
                        ManualImportLogic.ReleaseGroupOf(proposal.Path, target.MediaTypeId),
                        ManualImportLogic.ProblemOf(target.MediaTypeId, target.MediaSubtypeId, proposal, loaded.Units, loaded.Chapters, target.HasMedia) ?? (seconds.Contains(proposal.Path) ? ManualImportLogic.LargerFileChosen : null));
                }

                // A file the title's own kind does not take (a video of a book) is not that title's.
                foreach (var file in group.Where(file => !detected.ContainsKey(file.RelativePath)))
                {
                    detected[file.RelativePath] = new ManualImportFileV1(file.RelativePath, file.SizeBytes, null, null, null, null, null, handled.Contains(file.RelativePath),
                        reasons.GetValueOrDefault(file.RelativePath), mediaTypeId: ManualImportLogic.KindOf(file.RelativePath), problemKey: ManualImportLogic.WorkUnknown);
                }
            }

            return [.. media.Select(file => detected[file.RelativePath])];
        }

        // A chosen folder the move emptied goes; one that still holds something stays, and so does one that cannot be changed.
        public static async Task RemoveIfEmptyAsync(StorageFiles source, string directory, CancellationToken ct)
        {
            try
            {
                await source.DeleteDirectoryAsync(directory, ct);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string? BookLanguages(string path) => ReleaseWords.ReadLanguages(Path.GetFileNameWithoutExtension(path), "") is { Length: > 0 } languages ? string.Join('+', languages) : null;

        // The films, series and books of the library one of whose titles contains a searched word; Recognise decides which of them a file is.
        private static async Task<IReadOnlyList<ManualImportCandidate>> ReadCandidatesAsync(LogicContext context, IReadOnlyList<string> words, CancellationToken ct)
        {
            var patterns = words.Take(400).Select(word => "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%").ToArray();
            var rows = await context.ExecuteBatchAsync((ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        wanted."Id" AS "WantedItemId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        work."ReleaseYear"::integer AS "ReleaseYear",
                        ARRAY(
                            SELECT
                                title."Title"
                            FROM
                                "WorkTitles" AS title
                            WHERE
                                title."WorkId" = work."Id"
                        ) AS "Titles"
                    FROM
                        "WantedItems" AS wanted
                        JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    WHERE
                        work."MediaTypeId" IN (1, 2, 3)
                        AND (
                            work."CanonicalTitle" ILIKE ANY(@Patterns)
                            OR EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkTitles" AS title
                                WHERE
                                    title."WorkId" = work."Id"
                                    AND title."Title" ILIKE ANY(@Patterns)
                            )
                        )
                    ORDER BY
                        wanted."Id"
                    LIMIT 1000;
                    """;
                sql.Parameters.Add(new ManualImportTitleSearchData(patterns));
                return ctx.ReadSql.ReadListAsync<ManualImportCandidateData>(sql, token);
            }, ct);
            return [.. rows.Select(row => new ManualImportCandidate(row.WantedItemId, row.MediaTypeId, row.MediaSubtypeId, row.ReleaseYear,
                [row.CanonicalTitle, .. row.Titles.Where(title => !string.Equals(title, row.CanonicalTitle, StringComparison.Ordinal))]))];
        }

        // The film, series or book of every choice: the one the import is for and the others a person gave a file.
        public static async Task<Dictionary<long, ManualImportTarget>> ReadTargetsAsync(LogicContext context, long? defaultWantedItemId, IEnumerable<ManualImportChoiceV1> choices, CancellationToken ct)
        {
            var targets = new Dictionary<long, ManualImportTarget>();
            var ids = choices.Select(choice => TargetOf(choice, defaultWantedItemId));
            foreach (var id in (defaultWantedItemId is { } own ? ids.Append(own) : ids).Distinct())
            {
                targets[id] = await ReadTargetAsync(context, id, ct);
            }

            return targets;
        }

        // The film, series or book a file goes to: its own, else the download's; a file of a folder must name one.
        public static long TargetOf(ManualImportChoiceV1 choice, long? defaultWantedItemId) =>
            choice.WantedItemId ?? defaultWantedItemId ?? throw new DataValidationException(nameof(ManualImportChoiceV1.WantedItemId), "required");

        // The qualities a person gave, each one a quality its film, series or book can have; a book's file without one takes its format.
        public static Dictionary<string, string> ReadQualities(IEnumerable<ManualImportChoiceV1> choices, WantedTargetData target)
        {
            var qualities = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var choice in choices)
            {
                if (choice.QualityKey is null)
                {
                    if (target.MediaTypeId == MediaType.Book && ManualImportLogic.QualityOf(target.MediaTypeId, target.MediaSubtypeId, choice.Path) is { } format) qualities[choice.Path] = format;
                    continue;
                }

                if (!ProfileQualities.For(target.MediaTypeId, target.MediaSubtypeId).Contains(choice.QualityKey))
                {
                    throw new DataValidationException(nameof(ManualImportChoiceV1.QualityKey), "invalid_value");
                }

                qualities[choice.Path] = choice.QualityKey;
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

        // The numbers of a series' or book's files, the way every plan takes them; a film needs none.
        public static (string Path, int? Season, int? Episode)[] Numbers(IEnumerable<ManualImportChoiceV1> choices) => [.. choices.Select(choice => (choice.Path, choice.Season, choice.Episode))];

        // Places the chosen files, each as the film, episode, volume or chapter its owner says, grouped by the title they go to; the files of one waiting download may go to several. What a person must settle
        // comes back as an unavailable service when nothing was placed, a mistake in the choice as invalid data; when some files were placed, each file's outcome says what became of it. A waiting download
        // leaves the queue once every file of it is imported or the person finishes it; until then it keeps the files already imported.
        public static async Task<ManualImportResultV1> RunAsync(LogicContext context, DownloadImport import, long? wantedItemId, StorageFiles source, string directory,
            IReadOnlyList<StorageFile> files, bool isReadOnly, BaseDataList<ManualImportChoiceV1> choices, long? accountId, ManualImportDownload? download, CancellationToken ct)
        {
            var chosen = choices.Where(choice => choice.Imports).ToArray();

            // A download may be finished without a further file once a person imported some of it.
            var finishOnly = chosen.Length == 0 && download is { Finishes: true, Handled.Count: > 0 };
            if (!finishOnly && (chosen.Length == 0 || chosen.Select(choice => choice.Path).Distinct(StringComparer.Ordinal).Count() != chosen.Length))
            {
                throw new DataValidationException(nameof(RunManualImportDataV1.Files), "unknown_reference");
            }

            // An import moves its files, which a read-only connection does not allow.
            if (isReadOnly && !finishOnly)
            {
                throw new DataValidationException(nameof(RunManualImportDataV1.RelativePath), "import_source_read_only");
            }

            // Plan every group before anything is placed.
            var targets = await ReadTargetsAsync(context, wantedItemId, chosen, ct);
            var plans = new List<(ManualImportTarget Loaded, ManualImportChoiceV1[] Choices, IReadOnlyList<ImportPlacementData> Placements, Dictionary<string, string> Qualities,
                Dictionary<string, ChosenLanguages> Languages)>();
            foreach (var group in chosen.GroupBy(choice => TargetOf(choice, wantedItemId)))
            {
                var loaded = targets[group.Key];
                var target = loaded.Target;
                var members = group.ToArray();
                var qualities = ReadQualities(members, target);
                var placements = ImportPlacementLogic.PlanChosen(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, files, Numbers(members), loaded.Patterns, loaded.Units,
                    qualities, ImportPlacementLogic.KnownValues(target), loaded.Chapters);
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
            var finishes = download is not null && wantedItemId is { } own && ManualImportLogic.CompletesDownload(targets[own].Target.MediaTypeId, files, handled.Concat(chosen.Select(choice => choice.Path)),
                download.Finishes);
            for (var index = 0; index < plans.Count; index++)
            {
                var (loaded, members, placements, qualities, languages) = plans[index];
                var target = loaded.Target;
                var isMovie = target.MediaTypeId == MediaType.Movie;

                // A video is named by its release facts, those of the download's own release when it is placed for the title it was grabbed for (as an import by itself keeps them), else by
                // its own name; a book's file by its own name, with its format as its quality.
                var ownRelease = download is not null && target.WantedItemId == wantedItemId && target.MediaTypeId != MediaType.Book;
                ReleaseFact? FactsOf(string path) => target.MediaTypeId == MediaType.Book ? ReleaseFacts.WithQuality(null, path, qualities.GetValueOrDefault(path))
                    : ReleaseFacts.WithQuality(ownRelease ? ReleaseFacts.OfRelease(download!.ReleaseTitle, isMovie) : ReleaseFacts.OfPath(path, isMovie), path, qualities.GetValueOrDefault(path));

                // Only the last group finishes the download, and only when nothing before it went wrong.
                var finishesHere = finishes && index == plans.Count - 1 && firstFailure is null;
                var commit = loaded.Root is { } root
                    ? await import.CommitAsync(context, new ImportCommitRequest(root, target.WorkId, null, placements, source, directory, files, FactsOf, download?.OperationId, accountId, target.MediaTypeId,
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
                    languages.GetValueOrDefault(member.Path)?.Subtitles, commit.Targets?.GetValueOrDefault(member.Path), commit.KeptSources?.Contains(member.Path) == true)));
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

    // The films, series and books a folder can be imported to.
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
                    (@AnyMediaType OR work."MediaTypeId" IN (1, 2, 3))
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

    // The film, series or book a manual import is for.
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
                    AND work."MediaTypeId" IN (1, 2, 3);
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadOptionalAsync<ManualImportWorkV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("Only a film, a series or a book can be imported by hand.");
        }
    }

    // Every video, ebook and comic of a folder with the film, series or book of the library it was recognised as, what its name says, the name it would get and what a person must check.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/files")]
    public sealed class ReadManualImportFilesV1 : AdminService<ManualImportFolderParametersV1, ReadManualImportDataV1>
    {
        private readonly StorageAccess _storage;

        internal ReadManualImportFilesV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ManualImportFolderV1>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportFolderParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportFolderParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (_, files, isReadOnly) = await ManualImport.ReadFolderAsync(Context, _storage, Data.StorageMountId, Data.RelativePath, CancellationToken);
            var detected = await ManualImport.DetectAsync(Context, files, StoragePath.Name(Data.RelativePath), null, new HashSet<string>(), new Dictionary<string, string>(), CancellationToken);
            return new ManualImportFolderV1(new BaseDataList<ManualImportFileV1>(detected), isReadOnly);
        }
    }

    // The names the library would give the chosen files as their owners decided, and the film, series or book each one goes to.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/names")]
    public sealed class PreviewManualImportV1 : AdminService<ManualImportFolderParametersV1, PreviewManualImportDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ManualImportNameV1>>()];
        protected override ModuleRequirement GetInstanceModules(ManualImportFolderParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportFolderParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A file without its title has no name yet.
            var chosen = Data.Files.Where(choice => choice.WantedItemId is not null).ToArray();
            var targets = await ManualImport.ReadTargetsAsync(Context, null, chosen, CancellationToken);
            var names = new List<ManualImportNameV1>();
            foreach (var group in chosen.GroupBy(choice => choice.WantedItemId!.Value))
            {
                var loaded = targets[group.Key];
                var target = loaded.Target;
                var members = group.ToArray();
                var named = ManualImportLogic.NameChosen(target.MediaTypeId, target.MediaSubtypeId, target.CanonicalTitle, target.ReleaseYear, ManualImport.Numbers(members),
                    ManualImport.ReadQualities(members, target), loaded.Patterns, loaded.Units, ImportPlacementLogic.KnownValues(target), loaded.Chapters);
                ManualImport.ReadLanguages(members);
                names.AddRange(members.Select(member => new ManualImportNameV1(member.Path, group.Key, target.MediaTypeId, target.CanonicalTitle, named.GetValueOrDefault(member.Path), target.MediaSubtypeId)));
            }

            return new BaseDataList<ManualImportNameV1>(names);
        }
    }

    // Moves the chosen files of a folder into the library, each as the film, episode, volume or chapter its owner says.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/manual-import/import")]
    public sealed class RunManualImportV1 : AdminService<ManualImportFolderParametersV1, RunManualImportDataV1>
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
        protected override ModuleRequirement GetInstanceModules(ManualImportFolderParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ManualImportFolderParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (source, files, isReadOnly) = await ManualImport.ReadFolderAsync(Context, _storage, Data.StorageMountId, Data.RelativePath, CancellationToken);
            var result = await ManualImport.RunAsync(Context, _import, null, source, Data.RelativePath, files, isReadOnly, Data.Files, Auth.AccountId, null, CancellationToken);

            // The chosen folder goes too once the move emptied it, unless it is the connection's own top.
            if (Data.RelativePath.Length > 0 && result.Imported > 0)
            {
                await ManualImport.RemoveIfEmptyAsync(source, Data.RelativePath, CancellationToken);
            }

            return result;
        }
    }

    // The finished files of a download that waits for a person, recognised as its own title.
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
            var (download, reference, source, isReadOnly, folder) = await ManualImport.OpenDownloadAsync(Context, _storage, Parameters.OperationId, CancellationToken);
            var files = await ManualImport.ListFolderAsync(source, reference.Directory, CancellationToken);
            var handled = ImportProposal.ReadHandled(download.AttentionProposal).ToHashSet(StringComparer.Ordinal);
            var detected = await ManualImport.DetectAsync(Context, files, StoragePath.Name(reference.Directory), download.WantedItemId, handled, ImportProposal.ReadReasons(download.AttentionProposal),
                CancellationToken);
            return new ManualImportDownloadV1(download.WantedItemId, new BaseDataList<ManualImportFileV1>(detected), folder, isReadOnly);
        }
    }

    // Moves the chosen files of a waiting download; it leaves the queue once every file of it is imported or the person finishes it, and its client then lets go of the job.
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
            var (download, reference, source, isReadOnly, _) = await ManualImport.OpenDownloadAsync(Context, _storage, Parameters.OperationId, CancellationToken);
            var files = await ManualImport.ListFolderAsync(source, reference.Directory, CancellationToken);
            var handled = ImportProposal.ReadHandled(download.AttentionProposal);
            var result = await ManualImport.RunAsync(Context, _import, download.WantedItemId, source, reference.Directory, files, isReadOnly, Data.Files, Auth.AccountId,
                new ManualImportDownload(download.OperationId, handled, Data.FinishesDownload, download.AttentionProposal, download.ReleaseTitle), CancellationToken);

            // A finished download's client lets go of the job once the move left nothing an import would take; files nobody imported keep it.
            if (result.FinishedDownload)
            {
                await _import.RemoveFromClientAsync(Context, download.AcquisitionConnectionId, download.ExternalId, source, reference.Directory, reference.Directory.Length > 0, CancellationToken);
            }

            return result;
        }
    }
}
