namespace Jularr.Web.Features.Acquisition.Pipeline;

// The Anime side of the shared request lifecycle: it picks the wanted episodes, searches and grabs through the shared core and leaves downloading, importing and
// retrying to the Wanted pass, like the other media types.
public sealed partial class AnimeAcquisitionEngine
{
    public const string OperationKind = "anime-usenet-download";
}
