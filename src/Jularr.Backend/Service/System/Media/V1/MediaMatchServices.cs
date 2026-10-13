using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;

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
                    var seasons = target.MediaTypeId == MediaType.Series ? await Context.ExecuteBatchAsync((ctx, ct) => AniListMatcher.ReadSeasonsAsync(ctx, target.WorkId, ct), CancellationToken) : [];
                    var result = await _matcher.MatchAsync(target, seasons, null, CancellationToken);
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
}
