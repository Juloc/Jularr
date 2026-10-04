# Text-to-speech architecture

Issue: #317

Jularr uses one provider-neutral speech contract. Reader, learning and future native clients must not bind directly to one vendor or one model family.

## Provider order

The canonical fallback order is:

1. explicitly selected provider/voice when it is available and language-compatible
2. installed offline-neural provider
3. platform/device TTS
4. explicitly configured cloud provider
5. unavailable

The resolver uses BCP-47 language tags. Voice matching is deterministic: exact locale first, then base language. A platform provider may fall back to its own default voice selection rather than selecting a known wrong-language voice.

## Phase 1: browser Device TTS

`wwwroot/js/tts.js` wraps the browser Web Speech API behind `JularrTts.DeviceSpeechProvider`.

It provides:
- asynchronous voice discovery including `voiceschanged`
- language/voice selection
- bounded long-text chunking
- rate, pitch and volume controls
- play, stop, pause and resume
- utterance and boundary events with offsets in the original input text
- session cancellation so an old queue cannot continue after Stop or a new Speak request

It has no Reader-specific state. `speakSequence(items, options)` speaks an array or lazily pulled iterable of items as one cancellable session: only the current utterance plus `lookAhead` upcoming utterances are ever handed to the platform queue, and item-level `itemstart` / `itemend` / `boundary` events carry the caller's key. `resolveSpeech` is the browser mirror of `SpeechPreferenceResolver` / `SpeechAvailabilityResolver` (same provider order, voice matching and unavailable reasons), because device voices only exist on the client.

Important privacy distinction: Jularr's browser Device provider itself sends no TTS request to the Jularr server and makes no third-party HTTP request. Whether a platform/browser voice is fully local is controlled by that operating system/browser. `SpeechSynthesisVoice.localService` is exposed as metadata but must not be treated as a universal privacy guarantee. Only an Jularr-managed offline-neural model is classified as `GuaranteedOffline`.

## Phase 2: Reader integration

The shared Reader owns the UI integration; the speech engine stays Reader-agnostic. `wwwroot/js/reader-tts.js` mounts through the `reader-shell.js` extension API, so Books, Light Novels and Web Novels share one implementation. Its markup is only rendered for documents whose `ReaderCapabilities.SupportsTts` is set (reflowable text); manga and fixed documents get no controls.

Controls:
- toolbar Read button (mobile: `Vorlesen` in the bottom action bar): reads the current text selection, otherwise continues from the first paragraph on screen; while active it toggles Pause/Resume
- readers using the shared reader frame (`data-reader-frame`, see docs/UNIFIED_READER.md) render their own entry points with `data-reader-tts-toggle`: the accent Play button in the bottom bar and "Read aloud" in the mobile tool row. Every toggle shares one state; the Play button swaps to Pause while speaking, and "Voice and speed" opens the `Vorlesen` settings tab
- all read-aloud text comes from the UI catalog (`reader.tts.*`, rendered by the shared settings partial)
- a player bar with Pause/Resume and Stop stays visible while reading, independent of the auto-hiding chrome
- low-frequency actions in the overflow menu: read the current paragraph or the visible page

Behavior:
- paragraphs are pulled lazily with a look-ahead of two utterances; Stop cancels the session and the platform queue
- the spoken paragraph is marked and, where the browser reports word boundaries, the spoken word is highlighted with the CSS Custom Highlight API (annotated paragraph DOM is not modified)
- the view follows the spoken text: continuous mode scrolls, paged mode turns pages through the shared page-edge event; manual scrolling suspends following for a few seconds
- reading continues across the paragraphs and pages of a chapter; at the chapter end it stops unless the explicit `ttsAutoContinueChapters` setting is on, in which case it opens the next chapter and continues from its first paragraph
- readers that keep only the pages around the current one in the document (PDF books) answer `jularr:reader-tts-next-page` with the next page's text layer, and reading continues there until the end of the document
- Pause cancels and Resume restarts from the last spoken word, because Web Speech `pause()` is unreliable on mobile browsers
- switching the visible language stops reading
- while a generated target-language view is still arriving through progressive translation (#834), TTS may read only validated completed translated blocks; it never speaks ephemeral deltas or silently falls back to a different source language
- if TTS catches the first untranslated block in that selected target-language view, it waits at that semantic boundary while the shared translation run is still active; when the block becomes validated it may continue from the same session/locator, while cancelled/failed translation stops with a normal unavailable/error state rather than reading mixed languages

Settings live in the canonical `ReaderPreference` store and use the normal scope cascade with per-field inheritance: `ttsProviderId`, `ttsRate`, `ttsPitch`, `ttsVolume`, `ttsAutoContinueChapters` and one voice per language (`ttsVoiceId:<bcp47>`, stored as a JSON map per scope in which every language entry inherits independently). The `Vorlesen` settings tab shows one voice choice per document language and explains when a stored voice is missing on this device, when no voice for the language is installed (the device default is used) or when the browser cannot speak. `ReaderSettingsSnapshot.SpeechPreferencesFor(language)` is the server-side bridge from these settings to the resolver for native clients.

State and privacy:
- the TTS cursor lives only in the page session; reader-tts.js never writes reading progress. Progress keeps its existing meaning (what is on screen), so a view that follows the voice is saved like any other scroll
- spoken text is never sent to the server or persisted; the chapter auto-continue hand-off stores only the next chapter id in `sessionStorage` for one minute

## Phase 3: offline neural

Use sherpa-onnx as the execution boundary. It supports multiple offline TTS model families, including VITS/Piper and Kokoro, and documents Android, iOS and WebAssembly builds.

The model manager must use explicit downloadable model packs. A manifest owns:
- provider/model id and model version
- supported language tags and voices
- required runtime/model files
- expected byte size
- SHA-256 for every downloaded artifact
- minimum compatible Jularr/runtime version

Activation is download -> verify -> atomic move. Partial or checksum-failed downloads never become selectable.

Model files stay outside the main web image/APK so server/Docker and Android release paths are not inflated by every language model.

### Server: manifest and profile preferences

`GET /api/client/v1/speech/models` serves the manifest as `ClientSpeechModelsResponse`
(`Jularr.Web.Features.Speech.SpeechModelManifest*`). It is owner-configurable without a
rebuild: drop a JSON file at `{dataRoot}/speech/tts-model-manifest.json` (for example
`/data/speech/tts-model-manifest.json` in the container); with no override present, the
bundled default (embedded in the assembly, zero models) is served. Every entry needs
`providerId`, `modelId`, `version`, at least one recognized BCP-47 language tag, and a
`files` list where every file has `name`, `url`, `sizeBytes` and a 64-character hex
`sha256`; an entry with any unparsable/unverifiable file is dropped entirely rather than
partially served, and a manifest never claims a language or voice it does not list.

`GET`/`PUT /api/client/v1/me/tts-preferences` is the native-client surface for
profile-level provider/voice/rate/pitch/volume (`ClientTtsPreferences`,
`Jularr.Web.Features.Speech.TtsPreferencesService`). This reuses the canonical Reader
preference "default" (profile, no book) scope row and its existing Tts* columns/rules
instead of a second store: the Reader's Vorlesen settings and a native client's speech
settings share the same profile-level provider/voice/rate/pitch/volume. `PUT` is a partial
update (omitted/null fields keep their value); `voiceLanguage` + `voiceId` sets or (with a
blank/absent `voiceId`) clears one language's stored voice without touching others.
Installed-model facts are device-only and never part of this contract.

## Native clients

Android (Phase 3 delivered under this issue):
- isolated `core-tts` module: the provider-neutral contract ported field-for-field from
  the server resolver (`SpeechPreferenceResolver`/`SpeechAvailabilityResolver`), a
  `SystemTtsProvider` over Android `TextToSpeech` (voice discovery, BCP-47 matching,
  rate/pitch/volume, utterance lifecycle and word-boundary callbacks), a `TtsSession` that
  drives one utterance at a time so Stop always cancels the whole queue and pause/resume
  are emulated by restarting the current utterance, and a provider-neutral
  `TtsModelManager` (download -> verify SHA-256 per file -> atomic activation; a staging
  directory is never mistaken for an installed model)
- `NeuralTtsProvider` is the sherpa-onnx contract. sherpa-onnx does not publish its
  Android AAR to Maven Central (see k2-fsa/sherpa-onnx#3981) or any resolvable Maven
  coordinate as of this writing, only as a manually downloaded GitHub release asset, so
  the real binding lives in the optional `core-tts-sherpa` module. It is excluded from the
  Gradle build (and CI) unless `-PjularrNeuralTtsEnabled=true` is passed; the rest of
  the app builds and works with zero offline-neural models either way. To enable it
  locally: download `sherpa-onnx-<version>.aar` from
  https://github.com/k2-fsa/sherpa-onnx/releases, place it unmodified at
  `clients/android/core-tts-sherpa/libs/sherpa-onnx.aar` (not committed to this
  repository), and build with the property set. Without that module (or the manifest
  listing no models), the offline-neural provider reports itself unavailable and the
  resolver falls through to Device/Cloud as usual.
- app-mobile consumes the contract: a native `TtsSettingsScreen` (provider choice,
  rate/pitch/volume synced through `/me/tts-preferences`, offline-model download/delete
  against the server manifest) and a `TtsCoordinator` wired into the native player's
  learning sheet (`NativePlayerScreen`/`LearningSheet`) as a Speak action for the current
  subtitle line and for a looked-up word; both stop automatically when the sheet closes.
  Spoken text is only ever handed to the local engine, never persisted or sent to the
  server.
- app-tv: not wired in this phase (the TV remote-control player surface does not yet have
  a word-lookup/learning sheet to hang a Speak action off; revisit alongside that work).
- native TTS build work remains outside the server/Docker release critical path (the
  Android CI job only triggers on `clients/android/**` / `design/player/**`, unaffected by
  server-only changes here)

iOS, when a native client exists:
- system provider uses `AVSpeechSynthesizer`
- offline provider follows the same model-manifest contract through sherpa-onnx iOS runtime

## Source references

- Web Speech API: https://developer.mozilla.org/docs/Web/API/SpeechSynthesisUtterance
- sherpa-onnx TTS model families: https://k2-fsa.github.io/sherpa/onnx/c-api/html/tts.html
- sherpa-onnx Android build: https://k2-fsa.github.io/sherpa/onnx/android/build-sherpa-onnx.html
- sherpa-onnx iOS build: https://k2-fsa.github.io/sherpa/onnx/ios/build-sherpa-onnx-swift.html
- sherpa-onnx WebAssembly TTS: https://k2-fsa.github.io/sherpa/onnx/tts/wasm/build.html
