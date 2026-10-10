// Transitional aliases. Monitoring is now media-type-agnostic (Jularr.Web.Features.Acquisition.
// Monitoring, generic MonitoringEngine/MonitoringStore/MonitoringState and the MonitoredUnitKey /
// MonitorSettings / WantedUnit shapes), with anime as one registration at episode granularity. The
// anime pipeline, calendar, API and existing tests still refer to the historical Anime-prefixed type
// names; these compile-time aliases keep them working while the canonical types are generic. There is
// a single source of truth per type — an alias is not a duplicate type. Consumers are renamed off
// these aliases in a follow-up.

global using AnimeEpisodeKey = Jularr.Web.Features.Acquisition.Monitoring.MonitoredUnitKey;
global using AnimeMonitorSettings = Jularr.Web.Features.Acquisition.Monitoring.MonitorSettings;
global using AnimeEpisodeInventory = Jularr.Web.Features.Acquisition.Monitoring.MonitoredUnitInventory;
global using AnimeWantedEpisode = Jularr.Web.Features.Acquisition.Monitoring.WantedUnit;
global using AnimeWantedReason = Jularr.Web.Features.Acquisition.Monitoring.WantedReason;
global using AnimeAutoGrabDecision = Jularr.Web.Features.Acquisition.Monitoring.AutoGrabDecision;
global using AnimeMonitoringState = Jularr.Web.Features.Acquisition.Monitoring.MonitoringState;
global using AnimeMonitoringStore = Jularr.Web.Features.Acquisition.Monitoring.MonitoringStore;
