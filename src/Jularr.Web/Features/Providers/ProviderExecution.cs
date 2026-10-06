namespace Jularr.Web.Features.Providers;

/// <summary>
/// How <see cref="ProviderExecutor"/> should run a provider call: how many
/// attempts, how to back off, whether to observe the rate-limit gate/pacing, and
/// whether to record health. Providers pick a policy that matches their needs; the
/// defaults are a sensible resilient baseline for a new provider family.
/// </summary>
public sealed record ProviderExecutionPolicy
{
    /// <summary>Total attempts including the first (1 disables retries).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>First retry delay; doubles each retry up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Fail fast with <see cref="ProviderRateLimitedException"/> while the gate is active.</summary>
    public bool HonorRateLimitGate { get; init; } = true;

    /// <summary>Minimum spacing between calls (token-bucket pacing); null disables pacing.</summary>
    public TimeSpan? MinSpacing { get; init; }

    /// <summary>Record success/failure into <see cref="ProviderHealthTracker"/>.</summary>
    public bool TrackHealth { get; init; } = true;

    /// <summary>Fail fast with <see cref="ProviderUnavailableException"/> while the circuit is open.</summary>
    public bool ShortCircuitWhenUnavailable { get; init; } = true;

    /// <summary>Retry HTTP 5xx responses (<see cref="ProviderExecutor.SendAsync"/> only).</summary>
    public bool RetryServerErrors { get; init; } = true;

    /// <summary>Pause used for a 429 without a usable Retry-After header.</summary>
    public TimeSpan DefaultRetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound applied to any resolved Retry-After.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(1);

    public static ProviderExecutionPolicy Default { get; } = new();
}

/// <summary>Thrown when a call is skipped because the provider's circuit is open.</summary>
public sealed class ProviderUnavailableException(string providerKey)
    : Exception($"Provider '{providerKey}' is temporarily unavailable (circuit open).")
{
    public string ProviderKey { get; } = providerKey;
}

/// <summary>Thrown when a call cannot start because the provider has no usable credential (not configured, or disabled by the admin).</summary>
public sealed class ProviderNotConfiguredException(string providerKey)
    : InvalidOperationException($"Provider '{providerKey}' has no usable credential.")
{
    public string ProviderKey { get; } = providerKey;
}

/// <summary>Thrown when the provider answered but refused the configured credential (HTTP 401 or 403). The cause carries the HTTP status.</summary>
public sealed class ProviderAuthenticationException(string providerKey, Exception innerException)
    : Exception($"Provider '{providerKey}' refused the configured credential.", innerException)
{
    public string ProviderKey { get; } = providerKey;
}

/// <summary>Thrown when a call is skipped because the provider is rate limited.</summary>
public sealed class ProviderRateLimitedException(string providerKey, TimeSpan retryAfter)
    : Exception($"Provider '{providerKey}' is rate limited; retry in {retryAfter.TotalSeconds:F0}s.")
{
    public string ProviderKey { get; } = providerKey;

    public TimeSpan RetryAfter { get; } = retryAfter;
}
