# Speech

Owner decisions: device synthesis, no server synthesis and no spoken-text API.
Speech packages are opt-in; nothing is downloaded on startup, catalog reads or
Reader opening. Non-commercial models may be recommended with their terms visible.
Android integration belongs to the client foundation in #1296.

## Model administration

`/Admin/Instance/Speech` is linked from Instance modules, not a new sidebar
destination. Existing `instance.settings` permissions protect administration;
Reading must be enabled for activation and device delivery. Disabling a module
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
format. Its array order is the recommendation order. At most 64 entries and
1 MiB of metadata are accepted. Entries require identity/version, language,
voice, license reference, HTTPS source, positive bounded size and SHA-256.
Invalid or duplicate entries cannot be installed; a malformed override fails
visibly instead of silently restoring the shipped recommendations.

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

## Remaining integration

The optional browser neural runtime and client-side verified model cache are
not connected. Activating a package makes its bytes available, not a neural voice
usable in this browser. A profile preferring neural speech gets an explicit
unavailable state here, not an automatic model/runtime download or silent fallback.
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
