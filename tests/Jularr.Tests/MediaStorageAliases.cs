// Test-compilation compatibility aliases for the canonical Library storage model.
// Production owns the same transitional names in Features/Library/MediaStorageAliases.cs;
// global usings are compilation-local, so the test assembly needs its own aliases.
global using MediaFile = Jularr.Web.Features.Library.StoredFile;
global using MediaAnalysis = Jularr.Web.Features.Library.MediaTechnicalAnalysis;
global using MediaAnalysisStream = Jularr.Web.Features.Library.MediaTrack;
global using MediaStreamKind = Jularr.Web.Features.Library.MediaTrackKind;
