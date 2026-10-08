using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingDiscovery;
using Jularr.Web.Features.ReadingSources;

namespace Jularr.Web.Features.ReadingAcquisition;

// A public full-text copy of the light novel on a web source. Only a source explicitly marked as public full text and an exact title match qualify; previews,
// shops and reference-only results stay discovery evidence and never reach acquisition.
public sealed class LightNovelWebDirectSource(NovelImportService webNovels, ReadingCatalogSearchService catalogSearch, ReadingSourceSettingsStore sourceSettings) : IDirectSource
{
    public const string SourceName = "web";

    public string Name => SourceName;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.LightNovel;

    public async Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(SearchIntent intent, CancellationToken cancellationToken)
    {
        ReadingSourceSettingsState settings;
        try
        {
            settings = await sourceSettings.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Source configuration is optional here; Usenet stays available when it cannot be read.
            return [];
        }

        var payload = new ReadingRequestPayload(intent.Title, intent.Aliases, intent.Creator);
        var queries = new[] { intent.Title }
            .Concat(intent.Aliases)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3);
        var found = new List<AcquisitionCandidate>();
        foreach (var query in queries)
        {
            var outcome = await catalogSearch.SearchLightNovelsAsync(settings, query, limit: 12, cancellationToken);
            foreach (var candidate in outcome.Candidates.Where(candidate => CanAutoImport(payload, candidate, settings)))
            {
                var name = $"{candidate.Title} [EPUB]";
                found.Add(new AcquisitionCandidate(name, ReadingSourceCatalog.GetRequired(candidate.Provider).Name, null, "direct", null, null, null, null, null, null, null, null, AnimeReleaseParser.Parse(name), [], null, null)
                {
                    Type = AcquisitionType.DirectImport,
                    Offer = new DirectOffer(SourceName, $"{candidate.Provider}:{candidate.ExternalId}", IdentityIsExact: true)
                });
            }
        }

        return [.. found.DistinctBy(candidate => candidate.Identity)];
    }

    public async Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken)
    {
        var separator = offer.Key.IndexOf(':');
        var definition = ReadingSourceCatalog.GetRequired(offer.Key[..separator]);
        var workId = await webNovels.ImportWorkAsync(definition.DirectImportUrl!(offer.Key[(separator + 1)..]), cancellationToken);
        return new AcquisitionExecution(AcquisitionRequestStatus.Completed, $"Imported a public copy from {definition.Name}.", ResultUrl: $"/Novels/Work/{workId}");
    }

    public static bool CanAutoImport(ReadingRequestPayload payload, ReadingCatalogCandidate candidate, ReadingSourceSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(settings);

        if (!candidate.IsPublicWebSource
            || !settings.IsEnabled(candidate.Provider)
            || !ReadingSourceCatalog.TryGet(candidate.Provider, out var definition)
            || !definition.SupportsDirectImport
            || definition.DirectImportUrl is null
            || !definition.IsValidExternalId(candidate.ExternalId))
        {
            return false;
        }

        // Never infer identity from a loose contains/prefix search result. At least one canonical
        // title or alias from the request must exactly match the result's title/native title after
        // the same normalization the Reading catalog uses for ranking.
        var names = new[] { payload.Title }
            .Concat(payload.Aliases ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name));

        return names.Any(name => ReadingCatalogSearch.MatchScore(name, candidate) >= 1000);
    }
}
