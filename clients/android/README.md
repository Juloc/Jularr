# Jularr Android clients

The first-party Android phone and Android TV clients of Jularr. They use the versioned app
services of the backend (`/api/v1`) and never the web pages, a cookie or a password.

The durable contract is [`docs/APP_API.md`](../../docs/APP_API.md); the layout of the
television is fixed in [`docs/mockups/tv/SPEC.md`](../../docs/mockups/tv/SPEC.md).

## Contract

- A client **pairs** as a device: it starts a pairing, shows the six-digit code and the
  account confirms it on a signed-in profile. The device then owns a device token.
- Every call sends `Authorization: Bearer <token>`. The server renews the token past half its
  lifetime and returns the new one with the answer, so a client keeps the token from the last
  answer.
- `GET /api/v1/instance/capabilities` tells the client which permissions and which instance
  modules work for it. The server owns that decision; the client only hides what it may not
  use.
- Failures arrive as problem details with a machine code (`code`), the key of the localized
  message (`resourceKey`) and a `traceId` for support. A client shows the localized message
  and keeps the code, never a stack trace.

## Modules

- `core-model`: the shared data contracts of the app services.
- `core-api`: the HTTP client of the app services, the device token and the error contract.
- `core-design`: the shared design tokens of the product. The instance serves them as
  `tokens.json`; a client reads them from there, so the desktop's colors, radii and type scale
  are the client's colors, radii and type scale.
- `app-tv`: the Android TV client. It shows the desktop's layout at TV scale, with the focus
  instead of the pointer: the rail with Home, Watchlist and Library, the profile and the
  settings of the app, the top row and the home page with the type capsule and the shelves of
  the profile.

The focus of the television follows the reading order: the rail first, then the top row, then
the content with its rows from top to bottom and the entries of a row from left to right. Every
area of the screen keeps its own chain of entries, so left and right walk the order of the area
instead of searching for the nearest control; up and down move between the rows. The edges of an
area hand the focus over to the neighbouring area: the content gives it back to the rail of the
current surface, and the rail hands it to the content.

The speech runtime follows in a later pull request. The phone client with its offline downloads is
a package of its own: the television plays from the instance and downloads nothing.

## Baseline

- AGP 9.4.1
- Gradle 9.8.0
- JDK 17
- compileSdk 36, targetSdk 36, minSdk 26

## Build

From this directory with Android SDK 36 available:

```bash
gradle :core-api:testDebugUnitTest :core-api:lintDebug :core-design:testDebugUnitTest :core-design:lintDebug :core-model:lintDebug :app-tv:testDebugUnitTest :app-tv:lintDebug :app-tv:assembleDebug
```

CI runs the same commands (`.github/workflows/android.yml`).
