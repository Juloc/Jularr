using System.Collections.Concurrent;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Web.Features.Acquisition.Search;

/// <summary>
/// The short-lived memory of what indexers returned and which of them asked to be left alone. It caches normalized candidate
/// evidence only, never a profile score, so rescoring after a profile change reuses the evidence; a known-empty query is remembered
/// the same way, which stops an identical Deep search from being repeated against an indexer that has nothing. Both are bounded in
/// size and age, and an explicit refresh reads past them.
/// </summary>
public sealed class SearchEvidenceCache(TimeProvider? clock = null)
{
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RefusedModeLifetime = TimeSpan.FromHours(1);
    private const int MaximumEntries = 512;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<AcquisitionCandidate> Releases)> evidence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Until, string Reason)> backoffs = new();
    private readonly ConcurrentDictionary<(Guid Entry, IndexerSearchMode Mode), DateTimeOffset> refusedModes = new();

    public bool TryGet(string key, out IReadOnlyList<AcquisitionCandidate> releases)
    {
        if (evidence.TryGetValue(key, out var entry) && time.GetUtcNow() - entry.At < EvidenceLifetime)
        {
            releases = entry.Releases;
            return true;
        }

        releases = [];
        return false;
    }

    public void Set(string key, IReadOnlyList<AcquisitionCandidate> releases)
    {
        var now = time.GetUtcNow();
        if (evidence.Count >= MaximumEntries)
        {
            foreach (var stale in evidence.Where(pair => now - pair.Value.At >= EvidenceLifetime).Select(pair => pair.Key).ToArray())
            {
                evidence.TryRemove(stale, out _);
            }

            if (evidence.Count >= MaximumEntries)
            {
                foreach (var oldest in evidence.OrderBy(pair => pair.Value.At).Take(MaximumEntries / 4).Select(pair => pair.Key).ToArray())
                {
                    evidence.TryRemove(oldest, out _);
                }
            }
        }

        evidence[key] = (now, releases);
    }

    /// <summary>Remembers that an indexer refused a structured search function, so the next searches go straight to the text fallback instead of repeating the refused request.</summary>
    public void RefuseMode(Guid entryId, IndexerSearchMode mode) => refusedModes[(entryId, mode)] = time.GetUtcNow() + RefusedModeLifetime;

    /// <summary>The capabilities without the structured functions this indexer recently refused; a function is tried again after <see cref="RefusedModeLifetime"/>.</summary>
    public IndexerCapabilities? WithoutRefusedModes(Guid entryId, IndexerCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var refused = capabilities.Modes.Keys.Where(mode => mode != IndexerSearchMode.Search && refusedModes.TryGetValue((entryId, mode), out var until) && until > now).ToArray();
        return refused.Length == 0 ? capabilities : capabilities with { Modes = capabilities.Modes.Where(pair => !refused.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) };
    }

    /// <summary>Remembers that an indexer asked for time (Retry-After) so it is not asked again before then; the wait is bounded.</summary>
    public DateTimeOffset BackOff(Guid entryId, TimeSpan? retryAfter, string reason)
    {
        var wait = retryAfter is { } requested && requested > TimeSpan.Zero ? requested : DefaultBackoff;
        var until = time.GetUtcNow() + (wait > MaximumBackoff ? MaximumBackoff : wait);
        backoffs[entryId] = (until, reason);
        return until;
    }

    public bool IsBackedOff(Guid entryId, out DateTimeOffset until, out string reason)
    {
        if (backoffs.TryGetValue(entryId, out var entry) && entry.Until > time.GetUtcNow())
        {
            until = entry.Until;
            reason = entry.Reason;
            return true;
        }

        backoffs.TryRemove(entryId, out _);
        until = default;
        reason = string.Empty;
        return false;
    }
}
