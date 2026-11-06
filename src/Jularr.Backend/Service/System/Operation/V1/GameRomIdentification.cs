using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Libretro;
using Jularr.Integrations.Logic.Libretro;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.System.Operation.V1
{
    // Hashes the ROMs of a scanned games library and assigns each to the single game of its title, or to a game of its own; I/O stays outside transactions.
    internal sealed class GameRomIdentification
    {
        private static readonly TimeSpan CatalogAge = TimeSpan.FromDays(30);
        private readonly LibretroClient _libretro;
        private readonly TimeProvider _time;

        public GameRomIdentification(LibretroClient libretro, TimeProvider time)
        {
            _libretro = libretro;
            _time = time;
        }

        public async Task IdentifyAsync(LogicContext context, long libraryRootId, StorageFiles storage, string directory, CancellationToken ct)
        {
            await HashAsync(context, libraryRootId, storage, directory, ct);
            await AssignAsync(context, libraryRootId, ct);
        }

        private async Task HashAsync(LogicContext context, long libraryRootId, StorageFiles storage, string directory, CancellationToken ct)
        {
            // A platform without its DAT keeps its files for the next scan.
            var catalogs = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var afterId = 0L; ;)
            {
                var files = await context.ExecuteBatchAsync((ctx, token) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            file."Id",
                            file."RelativePath",
                            file."SizeBytes",
                            file."ModifiedAt",
                            NULL::bigint AS "WorkId"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."LibraryRootId" = @LibraryRootId
                            AND file."Id" > @AfterId
                            AND file."ProbedAt" IS NULL
                            AND file."MissingSince" IS NULL
                            AND file."SizeBytes" <= @MaximumBytes
                            AND file."RelativePath" ~* @Pattern
                        ORDER BY
                            file."Id"
                        LIMIT 50;
                        """;
                    sql.Parameters.Add(new GameRomCursorData(libraryRootId, afterId, GameRomMatching.FilePattern, GameRomMatching.MaximumBytes));
                    return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, token);
                }, ct);
                if (files.Count == 0)
                {
                    return;
                }

                foreach (var file in files)
                {
                    afterId = file.Id;
                    var isArchive = Path.GetExtension(file.RelativePath).Equals(".zip", StringComparison.OrdinalIgnoreCase);
                    if (!isArchive && !await EnsureCatalogAsync(context, GameRomMatching.System(file.RelativePath)!, catalogs, ct))
                    {
                        continue;
                    }

                    string? sha1 = null;
                    try
                    {
                        await using var stream = await storage.OpenReadAsync(StoragePath.Combine(directory, file.RelativePath), ct);
                        using var archive = isArchive ? new ZipArchive(stream, ZipArchiveMode.Read) : null;
                        var entry = archive?.Entries.Where(item => item.Name.Length > 0).ToArray() is [var single] ? single : null;
                        var system = GameRomMatching.System(archive is null ? file.RelativePath : entry?.Name ?? "");
                        if (isArchive && system is not null && !await EnsureCatalogAsync(context, system, catalogs, ct))
                        {
                            continue;
                        }

                        if (system is not null && (entry is null || entry.Length <= GameRomMatching.MaximumBytes))
                        {
                            await using var content = entry?.Open() ?? stream;
                            await content.ReadExactlyAsync(new byte[GameRomMatching.HeaderBytes(system, entry?.Length ?? file.SizeBytes)], ct);
                            sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(content, ct));
                        }
                    }
                    catch (InvalidDataException)
                    {
                        // Broken archives stay unidentified.
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
                    {
                        // Unreachable files retry on the next scan.
                        continue;
                    }

                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.SaveStoredFileHashAsync(new StoredFileHashData(file.Id, file.SizeBytes, file.ModifiedAt, _time.GetUtcNow().UtcDateTime, sha1), token);
                        return true;
                    }, ct);
                }
            }
        }

        private async Task<bool> EnsureCatalogAsync(LogicContext context, RetroSystem system, Dictionary<string, bool> catalogs, CancellationToken ct)
        {
            if (catalogs.TryGetValue(system.Dat, out var known))
            {
                return known;
            }

            // A stale DAT still identifies when its refresh fails.
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadGameRomCatalogAsync(new GameRomCatalogNameData(system.Dat), token), ct);
            var now = _time.GetUtcNow().UtcDateTime;
            if (state is null || state.ImportedAt < now - CatalogAge)
            {
                try
                {
                    var dat = await _libretro.ReadDatAsync(system.Dat, ct);
                    var roms = JsonSerializer.Serialize(dat.Roms.Select(rom => new { rom.Sha1, rom.Name }));
                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.SaveGameRomCatalogAsync(new GameRomCatalogData(system.Dat, system.IgdbId, system.Name, system.Abbreviation, dat.Version, now, roms), token);
                        return true;
                    }, ct);
                    state = new GameRomCatalogStateData(now);
                }
                catch (LibretroException)
                {
                    // Without a DAT the files wait for the next scan.
                }
            }

            catalogs[system.Dat] = state is not null;
            return state is not null;
        }

        private static async Task AssignAsync(LogicContext context, long libraryRootId, CancellationToken ct)
        {
            var platforms = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadEmulatedPlatformsAsync(new EmulatedPlatformsData(GameRomMatching.EmulatedPlatforms), token), ct))
                .ToDictionary(platform => platform.IgdbId, platform => platform.Id);
            for (var afterId = 0L; ;)
            {
                var files = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadGameRomFilesAsync(new StoredFileCursorData(libraryRootId, afterId), token), ct);
                if (files.Count == 0)
                {
                    break;
                }

                // A verified dump names its title and platform; a ROM the browser plays names them by its file.
                var candidates = new List<(GameRomFileData File, string Title, long PlatformId)>();
                foreach (var file in files)
                {
                    afterId = file.StoredFileId;
                    long? platformId = file.GamePlatformId
                        ?? (GameRomMatching.System(file.RelativePath) is { } system && platforms.TryGetValue(system.IgdbId, out var known) ? known : null);
                    var title = GameRomMatching.Title(file.RomName ?? Path.GetFileNameWithoutExtension(file.RelativePath));
                    if (platformId is not null && GameRomMatching.Key(title).Length > 0)
                    {
                        candidates.Add((file, title, platformId.Value));
                    }
                }

                // The single game of a title takes the file, a title without one becomes its own game, several games of one title stay for manual review.
                var keys = candidates.Select(candidate => GameRomMatching.Key(candidate.Title)).Distinct(StringComparer.Ordinal).ToArray();
                var matches = keys.Length == 0 ? []
                    : (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadGameTitleMatchesAsync(new WorkTitleLookupData(keys), token), ct)).ToDictionary(match => match.Title, match => match.WorkId);
                foreach (var (file, title, platformId) in candidates)
                {
                    var key = GameRomMatching.Key(title);
                    var known = matches.TryGetValue(key, out var match);
                    if (known && match is null)
                    {
                        continue;
                    }

                    try
                    {
                        matches[key] = await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            var workId = match ?? (await ctx.CreateWorkAsync(new WorkCreationData(MediaType.Game, null, false, title), token)).Id;
                            await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.StoredFileId, workId, null, null, null, null, platformId, false), token);
                            return (long?)workId;
                        }, ct);
                    }
                    catch (DataValidationException)
                    {
                        // A file assigned meanwhile keeps its assignment.
                    }
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.TagGameRomReleasesAsync(new LibraryRootIdData(libraryRootId), token);
                return true;
            }, ct);
        }
    }
}
