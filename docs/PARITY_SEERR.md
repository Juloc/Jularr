# Seerr parity

Owner decisions of 19 October 2026 for Seerr (Overseerr and Jellyseerr). Jularr is its own request and Discover
front end; Radarr/Sonarr server settings fall away. Editions and languages follow the
[general rule](PARITY_SONARR.md#editions-and-languages-all-media-types). Build state is tracked in the
[feature matrix](FEATURE_MATRIX.md).

## Same

Requests for every media type with approval, request rules (limit per period, automatic approval, blocked media
types, selectable quality profiles) and overrides per account, a user's request of chosen seasons, notifications in
the app, Plex sign-in, Discover with shelves and a Home order per profile.

## Wanted

| Area | Seerr | Jularr today | Decision |
| --- | --- | --- | --- |
| Request rules | Movie and series limits and auto-approval apart | One limit for everything | Per media type in the request rules: limit, period, automatic approval; overridable per account |
| 4K | 4K requests as their own tier with their own Radarr/Sonarr | – | A further edition after the general rule, beside the standard one; permissions "request 4K" and "approve 4K automatically", its own profile |
| Issues | Report a problem, comment, resolve, reopen | – | Like Seerr (type, episode, comment, resolve, reopen), plus actions straight from the report: search again, block the release, replace the subtitle |
| Users | Import users from Plex, Jellyfin and Emby; Jellyfin/Emby sign-in | Plex sign-in | Import Plex friends as accounts now |
| Discover | Custom sliders (genre, studio, network, streaming service, keyword), hide available or requested titles, Discover and streaming region | Fixed shelves, Home order per profile | The Owner chooses: only the Owner sets up Discover, or profiles may. With the free choice every profile picks its region and builds its own Discover (for example top Netflix Germany, top series Indonesia, top worldwide) |
| Notifications | E-mail, Web Push and other agents per user | The in-app bell | E-mail and Web Push, chosen per user: requested, approved, available, declined, issue answered |

## Later

| Area | Seerr | Decision |
| --- | --- | --- |
| Users | Jellyfin and Emby sign-in and import | Later |
| Plex | Auto-request from the Plex watchlist | Later, with the Plex watchlist import list |

## Not

| Area | Seerr | Reason |
| --- | --- | --- |
| Blocklist | Content blocklist by title, tag, keyword or region | Not needed |
| Services | Radarr and Sonarr servers, 4K servers | Jularr acquires itself |
