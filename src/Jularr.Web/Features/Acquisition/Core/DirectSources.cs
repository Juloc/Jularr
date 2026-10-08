using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Search;

namespace Jularr.Web.Features.Acquisition.Core;

// A source that hands a media type's files over without a download client (a free catalog, an OPDS server, a web source): it finds candidates for a
// search intent and, when selection picks one, imports it itself. Its candidates compete with Usenet ones in the same selection.
public interface IDirectSource
{
    string Name { get; }

    MediaAcquisitionKind Kind { get; }

    // Candidates carry AcquisitionType.DirectImport and an offer naming this source.
    Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(SearchIntent intent, CancellationToken cancellationToken);

    // Imports the offer into the library and says where it ended; a failure throws, which counts the candidate as tried.
    Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken);
}
