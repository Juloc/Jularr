using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Subtitles;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Fetches the missing subtitles of one video file per call; returns whether there was one.
    public sealed class FetchSubtitlesV1 : SystemService<SubtitleParametersV1, NoData>
    {
        private readonly StorageOptions _storage;
        private readonly SubtitleProviders _providers;
        private readonly ILogger<FetchSubtitlesV1> _logger;

        internal FetchSubtitlesV1(IOptions<StorageOptions> storage, SubtitleProviders providers, ILogger<FetchSubtitlesV1> logger)
        {
            _storage = storage.Value;
            _providers = providers;
            _logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SubtitleParametersV1 parameters) => ModuleRequirement.All("Library", "Subtitles");
        protected override ServicePermission GetPermission(SubtitleParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (source, languages, providers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ReadSourceAsync(ctx, ct), await ReadLanguagesAsync(ctx, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Subtitles, ct)), CancellationToken);
            if (source is null)
            {
                return new UpdateResult(false);
            }

            // What the file already has: embedded streams and files beside it, forced ones excepted.
            var present = source.TrackLanguages.Select(LanguageCodes.ToTwoLetter)
                .Concat(source.SiblingPaths.Where(LibraryFileTypes.IsSubtitle).Select(PlaybackTracks.Describe).Where(sibling => !sibling.IsForced).Select(sibling => LanguageCodes.ToTwoLetter(sibling.Language)))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var missing = languages.Where(language => !present.Contains(language)).ToList();
            if (missing.Count > 0 && providers.Count > 0)
            {
                await FetchAsync(source, missing, providers);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveSubtitlesCheckedAsync(new SubtitleCheckData(source.Id, source.SizeBytes, source.ModifiedAt), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        private async Task FetchAsync(SubtitleSourceData source, List<string> missing, IReadOnlyList<AcquisitionConnectionData> providers)
        {
            string directory;
            try
            {
                directory = StorageFileSystem.ResolveDirectory(_storage.GetConnectionPath(source.StorageProtocolTypeId, source.StorageMountId, source.SharePath), source.RootRelativePath);
            }
            catch (NasMountException)
            {
                return;
            }

            var isEpisode = source.MediaTypeId == MediaType.Series;
            var stem = source.RelativePath[..^Path.GetExtension(source.RelativePath).Length];
            foreach (var provider in providers)
            {
                if (missing.Count == 0)
                {
                    break;
                }

                try
                {
                    // Best per language: the same release, then hearing aids only when nothing else fits, then the most used.
                    var query = new SubtitleQuery(isEpisode, source.ImdbId, source.TmdbId, (int?)source.ParentUnitNumber, (int?)source.UnitNumber, [.. missing]);
                    var offers = await _providers.SearchAsync(provider, query, CancellationToken);
                    foreach (var language in missing.ToArray())
                    {
                        var best = offers.Where(offer => offer.Language == language && !offer.IsForeignOnly)
                            .OrderByDescending(offer => Similarity(offer.ReleaseName, source))
                            .ThenBy(offer => offer.IsHearingImpaired)
                            .ThenByDescending(offer => offer.Downloads)
                            .FirstOrDefault();
                        if (best is null)
                        {
                            continue;
                        }

                        var bytes = await _providers.DownloadAsync(provider, best, CancellationToken);
                        if (!Encoding.UTF8.GetString(bytes).Contains("-->", StringComparison.Ordinal) && !PlaybackTracks.Decode(bytes).Contains("-->", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        await SaveAsync(source, directory, stem + "." + language + ".srt", bytes);
                        missing.Remove(language);
                    }
                }
                catch (SubtitleException exception)
                {
                    _logger.LogWarning("Subtitle provider {Provider} failed: {Code}.", provider.Name, exception.Code);
                }
            }
        }

        // An existing file of that name stays; the new one belongs to the video's asset.
        private async Task SaveAsync(SubtitleSourceData source, string directory, string relativePath, byte[] bytes)
        {
            var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            var path = Path.Combine(StorageFileSystem.ResolveDirectory(directory, folder), Path.GetFileName(relativePath));
            if (File.Exists(path))
            {
                return;
            }

            await File.WriteAllBytesAsync(path, bytes, CancellationToken);
            var info = new FileInfo(path);
            var modifiedAt = DateTime.UnixEpoch.AddTicks((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveSubtitleFileAsync(new SubtitleFileData(source.LibraryRootId, relativePath, info.Length, modifiedAt, source.MediaAssetId), ct);
                return true;
            }, CancellationToken);
        }

        // Shared words of the release names, the group counting most.
        private static int Similarity(string? offer, SubtitleSourceData source)
        {
            if (offer is null || source.ReleaseName is null)
            {
                return 0;
            }

            var words = Words(source.ReleaseName);
            var score = Words(offer).Count(words.Contains);
            return source.ReleaseGroup is { } group && offer.Contains(group, StringComparison.OrdinalIgnoreCase) ? score + 10 : score;
        }

        private static HashSet<string> Words(string name) => [.. name.Split([' ', '.', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToLowerInvariant())];

        private static async Task<string[]> ReadLanguagesAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."SubtitleLanguages"::text[] AS "SubtitleLanguages"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return (await ctx.ReadSql.ReadRequiredAsync<SubtitleLanguagesData>(sql, ct)).SubtitleLanguages;
        }

        // Identified videos with a provider id, on storage that is not known to be offline; looked at again after a day.
        private static Task<SubtitleSourceData?> ReadSourceAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    file."SizeBytes",
                    file."ModifiedAt",
                    file."RelativePath",
                    file."ReleaseName",
                    file."ReleaseGroup",
                    file."MediaAssetId",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    work."MediaTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    (
                        SELECT
                            identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" = 'imdb'
                    ) AS "ImdbId",
                    (
                        SELECT
                            identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                        LIMIT 1
                    ) AS "TmdbId",
                    ARRAY(
                        SELECT
                            track."Language"::text
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 3
                            AND track."Language" IS NOT NULL
                    ) AS "TrackLanguages",
                    ARRAY(
                        SELECT
                            other."RelativePath"::text
                        FROM
                            "StoredFiles" AS other
                        WHERE
                            other."LibraryRootId" = file."LibraryRootId"
                            AND other."Id" <> file."Id"
                            AND other."MissingSince" IS NULL
                            AND starts_with(other."RelativePath", regexp_replace(file."RelativePath", '\.[^./]+$', '') || '.')
                    ) AS "SiblingPaths"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" AND asset."MediaAssetTypeId" = 1
                    JOIN "Works" AS work ON work."Id" = asset."WorkId" AND work."MediaTypeId" IN (1, 2)
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                WHERE
                    (file."SubtitlesCheckedAt" IS NULL OR file."SubtitlesCheckedAt" < now() - interval '1 day')
                    AND file."ProbedAt" IS NOT NULL
                    AND file."MissingSince" IS NULL
                    AND file."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                    AND coalesce(state."StorageAvailabilityTypeId", 1) = 1
                    AND (work."MediaTypeId" = 1 OR unit."WorkUnitTypeId" = 2 AND parent."Number" IS NOT NULL)
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" IN ('imdb', 'tmdb_movie', 'tmdb_tv')
                    )
                ORDER BY
                    file."SubtitlesCheckedAt" NULLS FIRST,
                    file."Id"
                LIMIT 1;
                """;
            return ctx.ReadSql.ReadOptionalAsync<SubtitleSourceData>(sql, ct);
        }
    }
}
