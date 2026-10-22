# Speech

Owner decisions: device synthesis, no server synthesis and no spoken-text API.
Speech packages are opt-in; nothing is downloaded on startup, catalog reads or
Reader opening. Non-commercial models may be recommended with their terms visible.
Android integration belongs to the client foundation in #1296.

## Model administration

`/Admin/Instance/Speech` is linked from Instance modules, not a new sidebar
destination. Existing `instance.settings` permissions protect administration;
Reading must be enabled for source refresh, activation and device delivery. Disabling a module
or package retains its data. Deactivation prevents subsequent server deliveries;
it does not revoke a copy a device already downloaded.

The server owns the ordered recommendations in `Logic/Speech/models.json`:
German, Indonesian, English. These are package metadata, not bundled weights.
The normal-quality ONNX packages are not advertised as benchmarked best voices.
Their archives contain the sherpa/Piper model and required support files.
The source release publishes their byte sizes and SHA-256 digests:
[sherpa-onnx TTS packages](https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models).

Dataset terms and attribution are linked in the catalog, not replaced by a
blanket claim that non-commercial use permits everything:

- [Thorsten model card](https://huggingface.co/rhasspy/piper-voices/blob/main/de/de_DE/thorsten/medium/MODEL_CARD): CC0 dataset.
- [News TTS training card](https://huggingface.co/datasets/rhasspy/piper-checkpoints/blob/main/id/id_ID/news_tts/medium/MODEL_CARD): CC-BY-NC-SA-4.0 dataset. This original card names the Indonesian news corpus; the converted model card's generic “See URL” is not treated as its license.
- [LJ Speech model card](https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/ljspeech/high/MODEL_CARD): public-domain dataset.

The Owner may replace the recommendations without rebuilding by writing
`DataPath/speech/catalog.json` (or `Media:SpeechPath/catalog.json`) with the same
format. Its array order is the recommendation order. At most 1024 entries and
2 MiB of metadata are accepted. Entries require identity/version, language,
voice, license reference, HTTPS source, positive bounded size and SHA-256.
Invalid or duplicate entries cannot be installed; a malformed override fails
visibly instead of silently restoring the shipped recommendations.

### Discovering more voices

The Owner approved dynamic discovery and explicit review of unclear model terms
on 22 October 2026. The same module page uses the shared admin list, details
drawer and forms. Search matches name, package identity, language and quality;
pages hold 25 packages. `GET /api/v1/admin/speech/models` returns `models`
(the shared `items`, `page`, `pageSize` result) and `languages` (the entire local
catalog's locale choices), with `query`, optional exact `language`, `gender`,
`isInstalled`, `page` and `pageSize` parameters. Filters are combined before
paging. Every UI filter has All; gender also has Unknown. Filters survive search,
paging and details/actions. Installation means the server archive exists with
its expected byte size, independent of activation, not the current device cache;
activation/download still checks SHA256. No filter performs external requests.

Gender metadata is optional (`0` unknown, `1` male, `2` female); a known value
requires an HTTPS `genderSource`. The upstream index has no structured gender
metadata, so discovered entries remain unknown, not inferred from names/audio.
Reviewed recommendations cite the [Thorsten dataset](https://www.openslr.org/95/)
and [LJSpeech research corpus description](https://pmc.ncbi.nlm.nih.gov/articles/PMC10395974/).
The shared htmx page-swap configuration disables its temporary old-attribute
settling pass: otherwise a focused search could retain its old empty value after
the server returned the new query. Ordinary SCSS transitions remain enabled.

`POST /api/v1/admin/speech/models/refresh` explicitly reads the fixed official
sherpa-onnx `tts-models` release metadata and the
[Piper voice index](https://huggingface.co/rhasspy/piper-voices/raw/main/voices.json).
Only cross-matched converted Piper/VITS archives with an exact published SHA256,
size within the browser's 128 MiB compressed limit, a locale accepted by the
canonical Speech field, speaker metadata
and a model-card reference are admitted. Raw Piper ONNX downloads, MD5-only
entries, missing metadata and other sherpa engines are not install options.
Packages with several speakers currently expose speaker 0, not a fabricated list
of all speakers. This is not every model on the internet or a per-device runtime
compatibility guarantee; archive/extraction/runtime checks still apply.

Only the two bounded JSON resources are fetched (20-second total timeout, 8/2 MiB
limits). Concurrent refresh clicks share one discovery with a one-minute cooldown.
The merged snapshot is atomically persisted at `SpeechPath/discovered.json`;
failed refreshes retain the saved snapshot and active package files. Reads and
device delivery never depend on an upstream request. Existing catalog entries
keep their reviewed terms and identities; discovery can add their sample link.
Previously pinned versions remain pinned if upstream changes or removes them:
refresh is discovery, not an implicit installed-model upgrade or removal.

The upstream index has no reliable structured license value. Newly discovered
entries therefore explicitly require model-card review, not a blanket free-use
claim. Activation with `requiresLicenseAcceptance` requires `acceptLicense: true`
on that invocation, before any package I/O. The page links the exact card and
shows the confirmation in its details drawer; disabling never needs confirmation.
The three reviewed recommendations retain their known terms and direct activation.
An Owner's explicit local catalog override may supply reviewed terms itself.

Samples use the official sherpa Piper prerecorded-sample URL for speaker 0.
The sample host is contacted only on Play; no text, weights or synthesis runtime
are loaded. One sample plays at a time and stops on navigation or hiding the page.
If upstream has no clip or playback fails, the page reports this without installing
anything. These samples are not a local custom-text synthesis test, which remains
a separate slice. `sampleUrl` accepts only the fixed HTTPS official sample host
and path. Samples may differ from the pinned package version after upstream changes.

Focused discovery coverage: `SpeechDiscoveryTests` checks source matching,
permissions, confirmation-before-download, verified delivery, paging and retaining
the snapshot on source failure. `SpeechPathTests.Catalog_…` checks the real controls,
exclusive sample playback and the shared drawer after failed actions on desktop
and phone. An explicit `JULARR_SPEECH_LIVE_METADATA=1` opts into official metadata
replay in that browser test and `SpeechDiscoveryTests.Source_ExplicitOfficialMetadataSmoke`;
normal CI uses synthetic metadata/audio only. Neither replay installs model weights.

`POST /api/v1/admin/speech/models/{hash}/activation` with `isEnabled` and
`version` downloads one selected package, checks its exact byte count and digest,
then publishes it atomically and commits activation in an authorization-checked
transaction. Downloads are serialized and time-limited, with no transaction held
during network I/O. Partial or invalid downloads are not activated. Reactivation
verifies a cached package again. Stale revisions are rejected.

`GET /api/v1/speech/models` lists only activated, locally available packages;
`GET /api/v1/speech/models/{hash}/file` serves those verified bytes without
triggering downloads. Clients must verify SHA-256 before caching/using them.
Archives are delivered, never unpacked or executed on the server.

## Profile and browser Reader

The canonical profile service at `GET/PUT /api/v1/profiles/{profileId}/speech`
stores speed, pitch and volume and up to 32 per-language provider/model/voice
choices in `Account.sql`. It checks the active profile, validates bounds, rejects
stale versions and preserves other languages. Clearing a device-voice choice
restores the default for that language. Neural choices require an activated,
language-compatible catalog model and its real voice identifier. Installed-device
facts never become server profile preferences.

Profile Settings and the Reader's Voice and speed panel use the same typed
`SpeechSettings`/`Form` fields. Real browser voice metadata fills the native
selects locally. Locale matching is exact, then language-compatible; a missing
device voice does not overwrite the saved choice. Browser device voices are not
advertised as guaranteed offline: the operating system may provide online voices.

The reflowable-text Reader has Read aloud, Pause/Resume and Stop. It reads the
selection once, otherwise from the visible paragraph to the end of this section.
One utterance at a time is generated lazily in bounded chunks. Pause cancels the
platform queue and resumes from the last word; obsolete callbacks cannot restart
a stopped or replaced session. Leaving stops; hiding pauses. Content/language
changes cancel speech; an unfinished translation cannot be spoken as mixed text.

CSS Custom Highlights mark the current word on canonical text and the existing
bounded decorative prints; no speech-specific text renderer or annotation DOM
mutations are introduced. Automatic following uses the current pagination and
only navigates when the word leaves view. Manual gestures temporarily suspend
following. The speech toolbar overlays the existing grid without shifting the
book and remains inside the Reader fullscreen element.

## Optional browser neural speech

The same profile/Reader fields select a device or neural provider and a real
activated model. Installation/removal are explicitly **device-local actions**,
separate from saving the profile voice choice. Cached models from this device
remain visible even when the server no longer offers their archive; deactivation
does not revoke a copy. An explicitly installed update replaces that model id's
cached version atomically. Missing/unsupported models fail visibly; neither
Read aloud nor opening the Reader downloads weights or silently changes provider.

An explicit device installation downloads one archive from the authorized server
endpoint, enforces its exact byte count and SHA-256, then decodes it in one Worker.
The MIT seek-bzip port retains its bzip2 CRC checks. Tar headers, truncation, paths,
links, duplicate models and expansion sizes are checked without extracting paths
to a filesystem. Only one ONNX and one UTF-8 token table are retained. Browser
limits: 128 MiB compressed, 384 MiB expanded, 192 MiB per model, 1 MiB tokens,
180 seconds total installation time. No cache commit precedes successful archive
verification. Failed/aborted writes roll back; leaving cancels installation.

IndexedDB stores verified, extracted model files plus their individual SHA-256
digests and original catalog identity/hash. Startup verifies the extracted bytes
again without repeatedly spending CPU on bzip2. Metadata reads never load every
model into memory. Storage/permission/quota failures are visible; device voices
remain available. Removing the current model stops this Reader's neural session.

The optional single-threaded sherpa/Piper WASM runtime and pronunciation data load
only after an explicit neural Read aloud. Text remains on the device. One bounded
chunk is synthesized at a time and played through Web Audio. Pausing playback
retains the current source; pausing initialization/generation terminates its
Worker and resumes the unheard chunk once. Stop, stale generations and navigation
cannot restart obsolete audio. Errors and finite deadlines release owned audio
and Worker references. Browser Worker shutdown notifications may lag termination;
immediate OS memory reclamation is not claimed.

Neural voices highlight the spoken chunk on canonical content and existing page
prints, not invented word timestamps. Independent pitch is unsupported: its
control is inert/ARIA-disabled while its typed form value remains bound, so the
saved device-voice pitch is retained. Rate and volume work for either provider.
Model caching/local synthesis is not a promise of offline Reader navigation or
physical-phone/Safari/Firefox compatibility; the native/offline client is separate.

## Browser runtime licenses

The Owner approved a GPLv3-compatible browser client on 20 October 2026,
including commercial reuse under GPL, without a blanket Server/project relicense.
See [the browser-client grant](../src/Jularr.Frontend/BROWSER-LICENSE.md) and
[pinned runtime sources, changes and build instructions](../src/Jularr.Frontend/Modules/Speech/Runtime/NOTICE.md).
The linked runtime includes GPLv3 eSpeak NG; Apache/MIT/other original notices are
retained, not relabelled. Models/datasets and separately supplied optional cores
retain their own terms; this does not override their non-commercial restrictions.
The Reader/settings link a readable complete notice bundle, built with a source
commit URL. Dirty/unversioned previews are marked development-only. Builds from a
source archive may specify `JULARR_BROWSER_SOURCE_REVISION`; it cannot hide a dirty
Git checkout or disagree with its revision. Redistributors must keep matching
sources available, including all engine/build inputs, not just offer dead links.

Normal builds use the committed, pinned runtime artifacts; they do not invoke
Docker, compile C++ or download models. The manual engine recipe preserves its
build directory for source inspection. `prepare-speech-runtime.mjs` documents and
checks the mechanical ESM/Uint8Array port and input hashes.

## Remaining integration

The Android adapter remains under #1296. Optional cross-chapter continuation and
speaking completed translation batches while later batches load remain tracked
under #1299; current speech stops at the section boundary and waits for a whole
translation. Auto-scroll is separate (#286), not an alias for read-aloud.

Focused checks: `SpeechTests` (catalog, download integrity, activation, permission
and module gates), `Tools/speech.test.mjs` (cancellation/queue/voice resolution),
`SpeechPathTests` (real profile saves, administration and Reader paint/selection/
controls on desktop/mobile; the device audio callbacks are deterministic test
doubles, not a claim of audible testing on physical phones).
The active instance database is never upgraded by this work; the fresh schema
baseline is tested only through isolated disposable databases.

`Tools/neural-speech.test.mjs` checks strict archive/CRC handling and neural
cancellation/chunk/pause/failure behavior. `SpeechPathTests.Neural_…` uses real
server activation/preferences, a clearly synthetic non-inference archive, real
browser archive/cache handling and Reader controls with an explicit inference
double. It is not an audible model-quality test.

Manual real-model replay after the frontend/test builds:
`node src/Jularr.Frontend/Tools/speech-neural-smoke.mjs <archive-directory> <catalog-id>`.
It never fetches external models: supply the catalog-hash-verified archives.
German Thorsten, Indonesian News TTS and English LJ Speech passed actual Worker
generation/Web Audio playback, pause/resume, cached reload, corrupt-cache rejection
and Worker cleanup in local Chromium. Evidence is under the ignored
`.scratch/speech-neural-spike/browser-integration-*.json`. Initial installation was
about 27/35/48 seconds here; extracted-file caching avoids paying that decompression
cost per Read aloud. Those are observations, not device/battery or voice-quality
guarantees. The test streams large packages through an ephemeral loopback fixture
server to avoid huge base64 copies through the debugging protocol; this is not
an additional application web host or synthesis service.
