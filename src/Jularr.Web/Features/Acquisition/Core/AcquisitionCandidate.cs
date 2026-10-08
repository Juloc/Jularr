using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Core;

// How the winning candidate reaches the library: queued in the download client, or imported straight from a direct source.
public enum AcquisitionType
{
    UsenetDownload,
    DirectImport
}

// What a direct source needs to import one candidate later: the source that found it and its own key for the file.
public sealed record DirectOffer(string Source, string Key);

// The one normalized candidate every source produces and the one selection compares, whatever the source is.
public sealed record AcquisitionCandidate(
    string Title,
    string? Indexer,
    int? IndexerId,
    string? Protocol,
    long? SizeBytes,
    int? Seeders,
    int? Leechers,
    DateTimeOffset? PublishedAt,
    int? AgeDays,
    double? AgeHours,
    string? Guid,
    string? InfoUrl,
    AnimeReleaseInfo ParsedRelease,
    IReadOnlyList<string> MatchedQueries,
    [property: JsonIgnore] Uri? InternalDownloadUri,
    [property: JsonIgnore] string? InternalMagnetUri)
{
    public AcquisitionType Type { get; init; } = AcquisitionType.UsenetDownload;

    public DirectOffer? Offer { get; init; }

    public string Identity =>
        Offer is not null
            ? $"direct:{Offer.Source}:{Offer.Key}"
            : !string.IsNullOrWhiteSpace(Guid)
                ? $"prowlarr:{IndexerId?.ToString() ?? "unknown"}:{Guid}"
                : $"release:{IndexerId?.ToString() ?? "unknown"}:{ParsedRelease.ReleaseKey}";

    /// <summary>Whether something can be done with the candidate: a download link for Usenet, an offer for a direct source.</summary>
    public bool IsAcquirable => Type == AcquisitionType.DirectImport ? Offer is not null : InternalDownloadUri is not null;

    /// <summary>
    /// Every indexer that returned this release, best source first. A search merges equivalent releases of several indexers into one
    /// logical candidate; this candidate's own fields are those of the first source.
    /// </summary>
    public IReadOnlyList<ReleaseSourceOption> Sources { get; init; } = [];

    /// <summary>The queries that found the release, per indexer, for the explanation Manual Search shows.</summary>
    public IReadOnlyList<QueryProvenance> Provenance { get; init; } = [];
}
