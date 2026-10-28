using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.AniBridge;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniBridge;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Anime, Manga and Light Novels find their AniList entries in small passes; unclear ones wait for a person.
    public sealed class MatchAniListV1 : SystemService<MediaMatchParametersV1, NoData>
    {
        private const int PassSize = 5;
        private readonly AniListMatcher _matcher;

        internal MatchAniListV1(AniListMatcher matcher)
        {
            _matcher = matcher;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaMatchParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(MediaMatchParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await Context.ExecuteBatchAsync((ctx, ct) => AniListMatcher.ReadTargetsAsync(ctx, null, PassSize, ct), CancellationToken);
            foreach (var target in targets)
            {
                // An unreachable AniList ends the pass; the Works stay due.
                try
                {
                    var (seasons, mappings) = target.MediaTypeId == MediaType.Series
                        ? await Context.ExecuteBatchAsync(async (ctx, ct) => (await AniListMatcher.ReadSeasonsAsync(ctx, target.WorkId, ct), await ctx.ReadAnimeMappingsOfWorkAsync(target.WorkId, ct)),
                            CancellationToken)
                        : ([], []);
                    var result = await _matcher.MatchAsync(target, seasons, mappings, null, CancellationToken);
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SaveIdentityMatchAsync(result, ct);
                        return true;
                    }, CancellationToken);
                }
                catch (ServiceUnavailableException)
                {
                    return new UpdateResult(false);
                }
            }

            return new UpdateResult(true);
        }
    }

    // Once a day the AniBridge mappings (AniList to TheTVDB, MIT) replace the loaded ones, while the AniList module is on. An unchanged release is not downloaded again;
    // a failed, unreadable or much smaller release leaves the loaded rows as they are.
    public sealed class SyncAnimeMappingsV1 : SystemService<MediaMatchParametersV1, NoData>
    {
        private readonly AniBridgeClient _client;

        internal SyncAnimeMappingsV1(AniBridgeClient client)
        {
            _client = client;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaMatchParametersV1 parameters) => ModuleRequirement.All("Library", "AniList");
        protected override ServicePermission GetPermission(MediaMatchParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var source = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAnimeMappingSourceAsync(ct), CancellationToken);
            AniBridgeRelease? release;
            try
            {
                release = await _client.ReadAsync(source.ETag, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException
                || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }

            if (release is null)
            {
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.MarkAnimeMappingsCheckedAsync(ct);
                    return true;
                }, CancellationToken);
                return new UpdateResult(false);
            }

            // A release with less than half the loaded rows is a broken one, not a smaller catalog.
            if (release.Mappings.Count * 2L < source.Rows)
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }

            var rows = JsonSerializer.Serialize(release.Mappings.Select(mapping => new
            {
                mapping.AniListId,
                mapping.TvdbId,
                mapping.SeasonNumber,
                mapping.AniListEpisode,
                mapping.FirstEpisode,
                mapping.LastEpisode
            }));
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveAnimeMappingsAsync(new AnimeMappingLoadData(rows, release.ETag, release.SchemaVersion, release.GeneratedAt?.UtcDateTime), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The relations AniList names for the entries of matched Works (sequel, side story, adaptation ...) become the Works' provider relations; a pass reads a few Works,
    // the oldest first. A limit or an unreachable AniList ends it and the Works stay due.
    public sealed class SyncAniListRelationsV1 : SystemService<MediaMatchParametersV1, NoData>
    {
        private const int PassSize = 5;
        private readonly AniListMetadata _aniList;

        internal SyncAniListRelationsV1(AniListMetadata aniList)
        {
            _aniList = aniList;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaMatchParametersV1 parameters) => ModuleRequirement.All("Library", "AniList", "Franchises");
        protected override ServicePermission GetPermission(MediaMatchParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadRelationTargetsAsync(PassSize, ct), CancellationToken);
            foreach (var target in targets)
            {
                // Entries AniList no longer knows are left out; the entries it does know decide the relations.
                var entries = new List<AniListMedia>();
                var type = AniListMatching.Type(target.MediaTypeId, target.MediaSubtypeId);
                try
                {
                    foreach (var id in type is null ? [] : target.ProviderIds)
                    {
                        try
                        {
                            entries.Add(await _aniList.ReadAsync(type!.Value, id, CancellationToken));
                        }
                        catch (KeyNotFoundException)
                        {
                        }
                    }
                }
                catch (ServiceUnavailableException)
                {
                    return new UpdateResult(false);
                }

                var relations = WorkRelationLogic.AniListTargets(entries, target.ProviderIds);
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    if (entries.Count > 0)
                    {
                        await ctx.SaveWorkRelationsAsync(new WorkRelationSyncData(target.WorkId, AniListMetadata.Namespace, relations), ct);
                    }

                    await ctx.MarkRelationsCheckedAsync(target.WorkId, ct);
                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
