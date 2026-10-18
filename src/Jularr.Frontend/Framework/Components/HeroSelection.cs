using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // One approved, profile-visible candidate. Source identity is canonical Work ID
    // where known, otherwise the provider namespace plus its stable external ID.
    // MediaGroup is a visible category slug (e.g. movies, anime or books).
    public sealed record HeroCandidate<T>(
        string Identity,
        string MediaGroup,
        DiscoverFeedType? Feed,
        bool IsContinue,
        T Content);

    // Pure picker: data gathering and authorization happen before this method.
    // Never triggers provider requests and never fabricates items to reach eight.
    public static class HeroSelection
    {
        public const int MaximumSlides = 8;
        public const int MaximumContinue = 2;

        public static IReadOnlyList<HeroCandidate<T>> Choose<T>(
            IEnumerable<HeroCandidate<T>> candidates,
            Random? random = null)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            random ??= Random.Shared;

            var available = candidates
                .Where(item => !string.IsNullOrWhiteSpace(item.Identity) && !string.IsNullOrWhiteSpace(item.MediaGroup))
                .ToList();
            var selected = new List<HeroCandidate<T>>(MaximumSlides);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Continue items are already sorted by the canonical progress query.
            foreach (var item in available.Where(item => item.IsContinue))
            {
                if (!seen.Add(item.Identity)) continue;
                selected.Add(item);
                if (selected.Count == MaximumContinue) break;
            }

            var groupCounts = selected.GroupBy(item => item.MediaGroup, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var feedCounts = new Dictionary<DiscoverFeedType, int>();
            var feeds = available.Where(item => !item.IsContinue && item.Feed is not null).ToList();

            while (selected.Count < MaximumSlides)
            {
                // Works can exist in multiple provider feeds. Their identity is
                // considered once regardless of category or source ranking.
                var remaining = feeds.Where(item => !seen.Contains(item.Identity)).ToList();
                if (remaining.Count == 0) break;

                // Best-effort balance among media groups with available candidates.
                var minGroupCount = remaining.Min(item => groupCounts.GetValueOrDefault(item.MediaGroup));
                var grouped = remaining.Where(item => groupCounts.GetValueOrDefault(item.MediaGroup) == minGroupCount).ToList();

                // And within equally represented groups prefer under-used feed modes.
                var minFeedCount = grouped.Min(item => feedCounts.GetValueOrDefault(item.Feed!.Value));
                var balanced = grouped.Where(item => feedCounts.GetValueOrDefault(item.Feed!.Value) == minFeedCount).ToList();
                var winner = balanced[random.Next(balanced.Count)];

                selected.Add(winner);
                seen.Add(winner.Identity);
                groupCounts[winner.MediaGroup] = groupCounts.GetValueOrDefault(winner.MediaGroup) + 1;
                feedCounts[winner.Feed!.Value] = feedCounts.GetValueOrDefault(winner.Feed!.Value) + 1;
            }

            return selected;
        }
    }
}
