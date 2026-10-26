# Android TV V1 – LAN connection and release acceptance

## LAN server discovery (opt-in)

Android TV sends exactly `JULARR_DISCOVER_V1` as IPv4 broadcast to UDP port **37812**. The Jularr frontend host now has an optional local-network UDP responder on that port. It returns JSON with `service: "jularr"`, `port`, `https`, `name` and `version`; it does **not** expose users, tokens, devices or media. It ignores malformed probes and non-private sender addresses and rate-limits replies to one per IP per second.

When using the repository `compose.yaml`, set `JULARR_TV_DISCOVERY=true` and, if different, `JULARR_TV_WEB_PORT` (default **8097**, the **host-side** exposed web port). Optional settings: `JULARR_TV_NAME` and `JULARR_TV_HTTPS=true` for an HTTPS endpoint available on that host and port. The example compose publishes `37812:37812/udp`, but the responder is off unless enabled. Do not forward UDP 37812 from the public internet; allow it on trusted local networks only.

For custom Compose/Dockhand configurations, add the same UDP port publishing and environment values `DeviceDiscovery__Enabled=true`, `DeviceDiscovery__WebPort=<LAN-facing HTTP(S) port>`, optionally `DeviceDiscovery__Https=true`. This requires that the container's UDP reply originates at the same LAN-accessible IP as the announced web port; reverse-proxy-only or VLAN-isolated deployments may need explicit routing. The TV must be on a network that permits IPv4 broadcasts. The manual server-address entry always stays available. Pairing and profile authorization still occur **after** discovery.

A true hardware acceptance run must confirm discovered address -> HTTP(S) connection -> device pairing code -> login/profile. Do not call LAN discovery verified merely because the UDP server and client unit tests compile.

## TV APK signing and updates

The `Release` workflow validates the official `vMAJOR.MINOR.PATCH[-prerelease]` tag on `main` and compares the TV source/dependencies with the last signed TV version. If the TV tree changed (or no prior signed APK exists), it tests and lints the tagged TV tree, signs a new APK, and then publishes the server image and GitHub Release. Otherwise it skips TV verification and signing, reuses the existing signed APK and checksum, and publishes the server image normally. The actual APK version stays encoded in its filename even when attached to a newer server release. Android signing is not triggered via a second `release:published` workflow because releases produced by GitHub's automatic `GITHUB_TOKEN` do not reliably emit a follow-on Actions event.

Configure repository secrets before publishing:

- `ANDROID_KEYSTORE_BASE64`: Base64 encoding of the stable Android signing keystore (do **not** commit it).
- `ANDROID_KEYSTORE_PASSWORD`: Keystore password.
- `ANDROID_KEY_ALIAS`: Signing key alias.
- `ANDROID_KEY_PASSWORD`: Key password.

A new TV signing job outputs `Jularr-TV-<version>.apk` and `Jularr-TV-<version>.apk.sha256` as GitHub release assets, matching `core-update/UpdateAssetResolver`. On server-only releases, those exact assets are copied unchanged from the last signed release, so `/releases/latest` still contains a valid TV package without suggesting a newer TV version. It verifies the signature and checks the SHA-256 *from the dist directory*, so downloading the APK and checksum side-by-side works without a nested `dist/` directory.

**Release gate:** A production Android APK cannot be validated or published without the actual secrets, and a real device must install/update a signed test release to prove PackageInstaller permission, signature continuity, download/checksum, version comparison and settings. Never reset the signing key on a routine update. The phone application is a distinct release track; this workflow publishes **TV only**.

## Physical-device evidence

Complete all [18 TV acceptance cases](TV_V1_PARITY_E2E_TESTPLAN.md) on an isolated Jularr server and an actual Android TV / Google TV device, including D-pad and back, multiaccount/Keystore, direct and HLS with real MediaAssetIds, audio selection, WebVTT, trickplay, chapter/skip, NAS wake and network loss. The current CI is JVM tests/lint and does not supply device screenshots or actual decoder results.

The Phone Companion (#1508) is developed in a separate Backend/Phone PR. Its eventual TV handshake must be integrated and device-tested without reintroducing the old `/api/client/v1` controller.
