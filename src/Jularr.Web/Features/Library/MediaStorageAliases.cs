// Transitional compile-time aliases: the canonical Library types are StoredFile,
// MediaTechnicalAnalysis and MediaTrack. These aliases do not create duplicate model types.
global using MediaFile = Jularr.Web.Features.Library.StoredFile;
global using MediaAnalysis = Jularr.Web.Features.Library.MediaTechnicalAnalysis;
global using MediaAnalysisStream = Jularr.Web.Features.Library.MediaTrack;
global using MediaStreamKind = Jularr.Web.Features.Library.MediaTrackKind;
