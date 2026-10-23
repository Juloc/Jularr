using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // What a search covers: Manga the asked volume, else like the automatic search the whole series as a pack or the next missing volume; Series one season or episode.
    internal static class WantedSearchScope
    {
        public static async Task<UnitScope?> ReadSearchScopeAsync(this LogicContext ctx, WantedTargetData target, int? parentUnitNumber, int? unitNumber, CancellationToken ct)
        {
            if (target.MediaSubtypeId == MediaSubtype.Manga)
            {
                var units = await ctx.ReadUnitStatesAsync(target.WantedItemId, ct);
                return unitNumber is { } volume ? AcquisitionSelectionLogic.ReadUnitScope(units, null, volume) : AcquisitionSelectionLogic.ReadNextUnitScope(units, null, firstAttempt: true);
            }

            if (target.MediaTypeId == MediaType.Series && parentUnitNumber is not null)
            {
                return AcquisitionSelectionLogic.ReadUnitScope(await ctx.ReadUnitStatesAsync(target.WantedItemId, ct), parentUnitNumber, unitNumber);
            }

            // An episode needs its season; other Works have no units.
            return unitNumber is null ? null : throw (target.MediaTypeId == MediaType.Series
                ? new DataValidationException("ParentUnitNumber", "required")
                : new DataValidationException("UnitNumber", "invalid_transition"));
        }
    }
}
