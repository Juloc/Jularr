# Jularr Android clients

The first-party Android phone and Android TV clients of Jularr. They use the versioned app
services of the backend (`/api/v1`) and never the web pages, a cookie or a password.

The durable contract is [`docs/APP_API.md`](../../docs/APP_API.md); the app surfaces this
directory needs are tracked in the native app contract issue.

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

The phone shell, the TV shell, the player, the offline downloads and the speech runtime
follow in later pull requests.

## Baseline

- AGP 9.4.1
- Gradle 9.8.0
- JDK 17
- compileSdk 36, targetSdk 36, minSdk 26

## Build

From this directory with Android SDK 36 available:

```bash
gradle :core-api:testDebugUnitTest :core-api:lintDebug :core-design:testDebugUnitTest :core-design:lintDebug :core-model:lintDebug
```

CI runs the same commands (`.github/workflows/android.yml`).
