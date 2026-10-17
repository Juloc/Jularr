using System.Text.Json;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;

namespace Jularr.Backend.Service.Core
{
    // A reproducible public request, without credentials or user/profile identifiers.
    internal sealed class DiscoverySnapshotRequest
    {
        public string Operation { get; init; } = "";
        public string Origin { get; init; } = "";
        public MediaType MediaType { get; init; }
        public MediaSubtype? Subtype { get; init; }
        public DiscoverFeedType? Feed { get; init; }
        public string? Language { get; init; }
        public string? Query { get; init; }
        public int? Year { get; init; }
        public int Page { get; init; } = 1;
        public int? Id { get; init; }
        public string? Namespace { get; init; }
        public string? ExternalId { get; init; }

        public int FreshHours(DiscoveryCachePolicyData policy) => Operation.EndsWith(".work", StringComparison.Ordinal)
            ? policy.MetadataFreshDays * 24 : Feed == DiscoverFeedType.Trending ? policy.MetadataTrendingHours : policy.MetadataRatingsHours;
        public string Serialize() => JsonSerializer.Serialize(this);
    }
}
