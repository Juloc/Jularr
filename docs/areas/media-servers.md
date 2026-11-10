# Media servers: Plex, Jellyfin and Emby

Plex instance mode and sign-in, Plex server PIN and Jellyfin/Emby progress sync.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- [PlexClient.cs](../../src/Jularr.Integrations/Logic/Plex/PlexClient.cs): bounded external protocol calls and user-scoped library access.
  [Data/Plex](../../src/Jularr.Integrations/Data/Plex): explicit protocol results;
  [ProviderUnavailableException.cs](../../src/Jularr.Integrations/Data/Common/ProviderUnavailableException.cs): shared integration error.
  [Integrations project](../../src/Jularr.Integrations/Jularr.Integrations.csproj): Data/Logic class library without Backend dependencies or a host;
  [AuthenticationOptions.cs](../../src/Jularr.Backend/Service/Core/AuthenticationOptions.cs): Backend-owned authentication policy/configuration.
- Plex instance mode: [PlexServerClient.cs](../../src/Jularr.Integrations/Logic/Plex/PlexServerClient.cs) (server libraries, items with provider ids,
  section refresh), [PlexServer.cs](../../src/Jularr.Backend/Service/Core/PlexServer.cs) (connection from the `plex` provider row, paged sync, refresh
  after import), [Plex server services](../../src/Jularr.Backend/Service/Admin/Provider/V1/PlexServerServices.cs) and
  [PlexLogic.cs](../../src/Jularr.Backend/Logic/Provider/PlexLogic.cs) (match by TMDB/IMDB/TVDB through `MatchWorksByIdentityAsync` in
  [WorkLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkLogic.cs), shared with Jellyfin and Emby, new Works for unknown titles, `plex` identities,
  unlink only after a complete pass); the System sync is the Plex part of the provider sync (`SyncProviderV1`, every 30 minutes unless the Owner chose
  another).
  Plex links count as available in Discover, Library and Details ("Open in Plex"); acquisition stays file-based.
- Jellyfin/Emby progress (module `ProgressSync`): [MediaServerClient.cs](../../src/Jularr.Integrations/Logic/MediaServer/MediaServerClient.cs) (one
  client, both dialects), [MediaServers.cs](../../src/Jularr.Backend/Service/Core/MediaServers.cs) (connection, users, paged two-way sync),
  [MediaServerServices.cs](../../src/Jularr.Backend/Service/Admin/Provider/V1/MediaServerServices.cs) and the Jellyfin and Emby parts of the provider
  sync (`SyncProviderV1`), [MediaServerLogic.cs](../../src/Jularr.Backend/Logic/Provider/MediaServerLogic.cs) (name proposals, the shared Work
  matching plus episode numbers, the merge rule); server addresses may carry a reverse-proxy path; an administrator confirms which profile a server
  user shares progress with on `/Admin/Providers`; test `MediaServerTests`.

## Tests

- Plex server connection by PIN: [PlexServerPinServices.cs](../../src/Jularr.Backend/Service/Admin/Provider/V1/PlexServerPinServices.cs), canonical
  [PlexAuthenticationLogic.cs](../../src/Jularr.Backend/Logic/Authentication/PlexAuthenticationLogic.cs); account/browser/purpose binding, expiry and
  replay: `AdminProviderTests.cs`.
