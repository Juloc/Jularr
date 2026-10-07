using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Policy;

/// <summary>
/// Moves the legacy Anime delay profiles that name no tag into the Acquisition Profiles they applied to, so the one profile owns the wait for every media
/// type. A delay profile scoped to a quality profile becomes a fallback tier of exactly that profile; one without any scope becomes a fallback tier of
/// every profile the Anime media type uses (its default and the profiles assigned to Anime works), and a delay profile that is more specific for a
/// profile wins there, as it always did. A profile that a tag-scoped delay profile can also apply to is left alone: for the Anime carrying the tag that
/// delay profile outranks the others, and a tier already merged into the profile would silently take its place. Applying it twice changes nothing, so a
/// restart between the two writes loses nothing.
/// Tag-scoped delay profiles and tag-scoped indexer restrictions stay in the legacy store: tags exist only on Anime monitoring, so they cannot become
/// profile assignments without inventing profiles; the Anime pipeline keeps translating them into the same profile fields at search time (compatibility
/// only; it goes away together with the Anime tag assignment).
/// </summary>
public static class LegacyDelayMigration
{
    public sealed record Plan(IReadOnlyList<QualityProfile> ChangedProfiles, IReadOnlySet<string> MigratedDelayIds);

    public static Plan Build(IReadOnlyList<QualityProfile> profiles, IReadOnlyList<AnimeDelayProfile> delays, IReadOnlySet<string> animeProfileIds)
    {
        var untagged = delays.Where(delay => delay.TagIds.Length == 0).ToArray();
        var changed = new List<QualityProfile>();
        var migrated = new HashSet<string>(untagged.Where(delay => delay.DelayMinutes <= 0).Select(delay => delay.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (delays.Any(delay => delay.TagIds.Length > 0 && (string.IsNullOrEmpty(delay.QualityProfileId) || delay.QualityProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var candidates = untagged.Where(delay => delay.DelayMinutes > 0 && (delay.QualityProfileId is { Length: > 0 } || animeProfileIds.Contains(profile.Id))).ToArray();
            if (AcquisitionDelayEngine.SelectProfile(candidates, profile.Id, []) is not { } applicable)
            {
                continue;
            }

            var merged = AcquisitionDelayEngine.WithDelayAsFallbackTier(profile, applicable);
            if (!ReferenceEquals(merged, profile))
            {
                changed.Add(merged);
                migrated.Add(applicable.Id);
            }
            else if (profile.FallbackTiers.Any(tier => tier.AfterMinutes == applicable.DelayMinutes))
            {
                migrated.Add(applicable.Id);
            }
        }

        return new Plan(changed, migrated);
    }
}

/// <summary>Runs <see cref="LegacyDelayMigration"/> once at startup; it is idempotent, so running it at every start is the whole restart story.</summary>
public sealed class AcquisitionPolicyMigration(AcquisitionPolicyStore policy, QualityProfileStore profiles, IServiceScopeFactory scopes, ILogger<AcquisitionPolicyMigration> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var state = await policy.LoadAsync(cancellationToken);
        if (!state.DelayProfiles.Any(delay => delay.TagIds.Length == 0))
        {
            return 0;
        }

        var qualityState = await profiles.LoadAsync(cancellationToken);
        var plan = LegacyDelayMigration.Build(qualityState.Profiles, state.DelayProfiles, await AnimeProfileIdsAsync(qualityState, cancellationToken));

        // Profiles first, the legacy entry second: a crash in between repeats the identical merge and then removes the entry.
        foreach (var profile in plan.ChangedProfiles)
        {
            await profiles.UpsertAsync(profile, cancellationToken);
        }

        if (plan.MigratedDelayIds.Count > 0)
        {
            await policy.UpdateAsync(current => current with { DelayProfiles = [.. current.DelayProfiles.Where(delay => !plan.MigratedDelayIds.Contains(delay.Id))] }, cancellationToken);
            logger.LogInformation("Moved {Count} legacy Anime delay profile(s) into Acquisition Profiles ({Profiles} profile(s) changed).", plan.MigratedDelayIds.Count, plan.ChangedProfiles.Count);
        }

        return plan.MigratedDelayIds.Count;
    }

    private async Task<IReadOnlySet<string>> AnimeProfileIdsAsync(QualityProfileState state, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.DefaultProfileIdFor(MediaAcquisitionKind.Anime) is { } defaultId)
        {
            ids.Add(defaultId);
        }

        var assigned = state.WorkAssignments.Where(pair => Guid.TryParse(pair.Key, out _)).ToDictionary(pair => Guid.Parse(pair.Key), pair => pair.Value);
        if (assigned.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var animeWorks = await db.Works.AsNoTracking().Where(work => work.MediaType == WorkMediaType.Anime && assigned.Keys.Contains(work.Id)).Select(work => work.Id).ToListAsync(cancellationToken);
            ids.UnionWith(animeWorks.Select(id => assigned[id]));
        }

        return ids;
    }
}
