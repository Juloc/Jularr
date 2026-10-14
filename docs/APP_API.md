# App API

Native apps (Android, Android TV, later clients) use the same versioned services as the web pages under `/api/v1`. They sign in as a paired device, never with a password, a personal API key or a browser cookie. The `Devices` module must be on.

## Versions

Every route carries its version (`/api/v1/...`) and every contract its suffix (`...V1`). A released V1 contract only gains optional fields; anything incompatible becomes a V2 route next to V1. `GET /api/v1/instance/capabilities` tells a signed-in app which modules and permissions it has.

## Pairing

1. `POST /api/v1/devices/pairings` with `{ "deviceName": "Living room TV", "devicePlatform": "AndroidTV" }` returns `pairingId`, the six-digit `code` and `expiresAt` (ten minutes). Keep `pairingId` secret; show the code and a QR code of `<address>/Devices/Pair?code=<code>`. The address is the instance's public address if one is set, otherwise the one the app used.
2. A signed-in phone opens that page, sees device name, platform and code, and confirms. Opening the page alone pairs nothing.
3. Every five seconds `POST /api/v1/devices/pairings/{pairingId}/claim` answers `{ "isPaired": false }` until the phone confirmed, then `{ "isPaired": true }` with the token in the `Jularr-Device-Token` response header. The claim works once.
4. An expired pairing answers 404: start a new one with `"replacesPairingId"` set to the old `pairingId`, which ends the old code at once.

Both pairing calls need no antiforgery token and set no cookie. Unauthenticated calls are limited per address.

## Requests

Send `Authorization: Bearer <token>` with every request; no cookies, no antiforgery token. Only paired device sessions are accepted.

- The device is a normal user of its profile: profile and module rights are checked on every request, administration and pairing further devices are not available, and requests made through a device paired to the Owner still wait for approval.
- `PUT /api/v1/auth/profile/{profileId}` or `POST /api/v1/auth/profile/{profileId}/pin` with `{ "pin": "..." }` switches to another profile of the account. The answer carries a new token in `Jularr-Device-Token`; the old one ends at once. Profiles cannot be created or deleted from a device.
- `PUT /api/v1/sessions/{sessionId}/device` with `{ "deviceName": "..." }` renames the device; `DELETE /api/v1/auth/session` ends it.
- A token stays valid while it is used: past half its lifetime a request renews it. Revoking the device in the account's or the Owner's session list, a password change or a disabled account end it at once.

## Errors

Failures are problem details with a `code` (for example `invalid_code`, `pairing_expired`, `device_pairing_attempts`, `rate_limit_exceeded`) and a `resourceKey` for its text. 401 means the token is unknown, expired or revoked: pair again. Thirty wrong tokens from one address within a minute block further tokens from it for that minute.
