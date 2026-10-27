# Jularr Android clients

This directory contains the first-party Android phone and Android TV clients defined by [`docs/ANDROID_CLIENTS.md`](../../docs/ANDROID_CLIENTS.md).

The active **Android TV** client authenticates with device-pairing bearer tokens and uses the clean-start `/api/v1` catalog, profile and playback contracts. The historic `/api/client/v1` documentation reflects the imported phone/legacy foundation, not the launched TV runtime. See [TV V1 audit](docs/TV_V1_FULL_AUDIT_2026-10-23.md).

## Modules

- `app-mobile`: phone shell; the full Jularr web UI remains the normal Library/Learn/Settings surface, while episode playback becomes native.
- `app-tv`: native V1 TV shell, pairing, catalog, Favorites, Discover and Media3 player. Dormant cookie-controller paths were removed from this module.
- `core-api`: shared historical mobile-client APIs plus the TV-specific bearer-authenticated `/api/v1` catalog, profile, playback and playback-extras adapters.
- `core-model`: shared immutable client models.
- `core-player`: Media3 player/session foundation and deterministic direct/fallback selection.
- `core-session`: shared playback-session/companion command models for the later pairing slice.
- `core-design`: packages the canonical JSON under `design/player` as Android assets. Those JSON files remain the source of truth.
- `core-tts`: voice-reading runtime abstraction, model management, and system/model speech synthesis.
- `core-update`: in-app GitHub release update check, download, checksum verification, and APK installation flow.
- `core-tts-sherpa`: optional offline neural TTS runtime using sherpa-onnx (opt-in via `-PjularrNeuralTtsEnabled=true`).

## Baseline

- AGP 9.4.1
- Gradle 9.8.0
- JDK 17
- Kotlin/Compose compiler 2.4.20
- compileSdk 36
- targetSdk 36
- minSdk 26
- Media3 1.11.1
- Compose BOM 2026.06.01 (BOM 2026.08.00+ and core-ktx 1.19+ need compileSdk 37)
- TV Material 1.1.0

The repository has separate CI workflows for [Android TV](../../.github/workflows/android.yml) and [Android phone](../../.github/workflows/android-mobile.yml). Each runs unit tests and lint only for its app and affected shared modules; changes limited to the phone do not run TV tests or builds. The [Release workflow](../../.github/workflows/release.yml) compares TV sources and dependencies against the last signed TV APK. Only a changed TV tree runs TV tests, lint and production signing; server-only releases carry forward the verified, previously signed TV APK and checksum without rebuilding it. Configure the four repository secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` before release. Missing signing secrets cause the TV publishing job to fail, rather than upload an unsigned APK. Preserve the signing key across versions or Android will reject upgrades.

## Test APK versus official install

Every successful TV-relevant Android PR CI run provides an Actions artifact named `jularr-tv-preview-debug`. Download it from the Android run's **Artifacts** section, unzip and sideload `app-tv-debug.apk` onto Android TV/Google TV to test pairing, playback, D-pad and Phone Companion on real hardware.

**Important:** This preview is signed with the ephemeral Android debug key, **not** the protected Jularr release keystore. It cannot upgrade into a production release in place. Uninstall it before installing the first official `Jularr-TV-<version>.apk`. Do not use debug builds for production or as proof of release-signing and in-app upgrade acceptance.

The production package is released only from a version tag on `main` after the release CI guard, signed with the existing four Android secrets, and uploaded with a matching `.sha256`. The TV Settings screen can check for this latest stable release, download/verify it, and start the Android system installer (the user must approve installation).

## Build

From this directory with Android SDK 36 available:

```bash
gradle :core-api:testDebugUnitTest :core-model:testDebugUnitTest :core-player:testDebugUnitTest \
  :core-session:testDebugUnitTest :core-design:testDebugUnitTest :core-tts:testDebugUnitTest \
  :core-update:testDebugUnitTest :app-mobile:testDebugUnitTest :app-tv:testDebugUnitTest
gradle :app-mobile:assembleDebug :app-tv:assembleDebug
```

The active TV app uses `/api/v1`; Wake-on-Demand is server-controlled and the TV retries only `storage_waking`. Android JVM/HTTP contract tests are not physical TV/Media3/NAS end-to-end verification. For device acceptance use [TV V1 E2E test plan](docs/TV_V1_PARITY_E2E_TESTPLAN.md).

## Parallel app ownership

Phone and TV are intentionally developed in parallel after this foundation is merged:

- **Agent A / phone** owns `app-mobile/**`.
- **Agent B / TV** owns `app-tv/**`.
- `core-api/**`, `core-model/**`, `core-player/**`, `core-session/**`, `core-design/**`, `core-tts/**`, `core-update/**`, Gradle root files, and `.github/workflows/android.yml` are shared infrastructure. An app agent must claim/coordinate a shared-path change before editing it.
- Neither app agent edits server/web paths as part of its app slice unless a separate non-overlapping claim is recorded.

This keeps phone and TV changes independently reviewable while both consume one server contract and one player model.
