using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.OfflineLibrary;

/// <summary>
/// Server-side entry point of smart offline prefetch (#415): loads the current
/// profile's policy, selects the next-up candidates from canonical progress and
/// lets <see cref="OfflinePrefetchPlanner"/> decide against the device's own
/// inventory. The client then downloads and deletes through the existing
/// offline endpoints; nothing here writes content or progress.
/// </summary>
public sealed class OfflinePrefetchService(
    OfflinePrefetchPolicyStore store,
    OfflinePrefetchCandidateSource candidates,
    CurrentAccountContext currentAccount,
    IInstanceModuleService? instanceModules = null)
{
    /// <summary>The profile's policy as it applies on this instance: episodes are video for playing, so a manager-only instance prefetches none; chapters keep their own switches.</summary>
    public async Task<OfflinePrefetchPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await store.LoadAsync(currentAccount.ProfileId, cancellationToken);
        return instanceModules is not null && !await instanceModules.IsEnabledAsync(InstanceModule.Playback, cancellationToken) ? policy with { IncludeEpisodes = false } : policy;
    }

    public async Task<(OfflinePrefetchPolicy Policy, OfflinePrefetchPlan Plan)> PlanAsync(
        OfflinePrefetchDeviceState device,
        CancellationToken cancellationToken = default)
    {
        var policy = await GetPolicyAsync(cancellationToken);
        if (!policy.Enabled)
        {
            return (policy, OfflinePrefetchPlanner.Plan(policy, [], device));
        }

        var next = await candidates.GetAsync(policy, cancellationToken);
        return (policy, OfflinePrefetchPlanner.Plan(policy, next, device));
    }
}

public static class OfflinePrefetchRegistration
{
    public static IServiceCollection AddOfflinePrefetch(
        this IServiceCollection services,
        string dataRoot = "/data")
    {
        services.AddSingleton(provider => new OfflinePrefetchPolicyStore(
            dataRoot,
            provider.GetService<ILogger<OfflinePrefetchPolicyStore>>()));
        services.AddScoped<OfflinePrefetchCandidateSource>();
        services.AddScoped<OfflinePrefetchService>();
        return services;
    }
}
