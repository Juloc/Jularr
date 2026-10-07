# Media segments and seek previews

Jularr stores one canonical set of skip markers per episode and generates
optional seek-preview thumbnails ("trickplay"). Web, PWA, Android and Android TV
players consume the same server descriptor; clients never compute their own
segment boundaries, confidence rules or thumbnails.

Source media is only read. Markers live in SQLite (`EpisodeMediaSegments`);
generated thumbnails live in a disposable cache under `/data`.

## Segment model

Each marker has:

| Field | Meaning |
| --- | --- |
| `kind` | `intro` (OP), `recap`, `outro` (ED), `preview`, `credits` (credits/other) |
| `startMs`, `endMs` | Episode time in milliseconds; a marker is at least 1 second long |
| `source` | `manual`, `imported`, `provider` or `detector` |
| `method`, `version` | How the marker was produced (for example `manual 1`, `sidecar 1`, a detector name and version) |
| `confidence` | 0–1. Manual markers are always 1 |

There is at most one marker per episode, kind and source.

### Precedence

For each kind the most authoritative source wins, regardless of confidence:

1. `manual` – owner corrections on **Episode → Edit skip segments**
2. `imported` – sidecar files (below)
3. `provider` – reserved for trustworthy metadata markers mapped to the exact episode (none are imported yet)
4. `detector` – automatic detection

Removing a manual marker falls back to the next source. An explicit correction
therefore always replaces an automatic result, and a missing marker is preferred
over a bad guess.

### Skip actions

A player offers **Skip intro / recap / outro / credits / preview** only while the
playback position is inside a resolved marker whose confidence is at least the
configured threshold. The action seeks to the canonical `endMs`. Nothing is
skipped automatically.

The threshold is part of the application configuration:

```json
{
  "MediaSegments": {
    "SkipConfidenceThreshold": 0.8,
    "FingerprintDetectionEnabled": false
  }
}
```

(`MediaSegments__SkipConfidenceThreshold` / `MediaSegments__FingerprintDetectionEnabled`
as environment variables.) Each
descriptor carries the threshold that was applied and a per-segment `canSkip`
flag, so clients do not re-implement the rule.

## Sidecar import format

Sidecar files are read (never written) during every library scan by
`MediaSegmentSidecarImporter`. They are the source of truth for `imported`
markers: a marker removed from the file is removed from Jularr; manual,
provider and detector markers are never touched. Re-scanning an unchanged file
changes nothing.

Two placements are supported:

**Per episode** – next to the media file, named after it:

```text
Season 01/
├── Frieren - S01E03.mkv
└── Frieren - S01E03.segments.json
```

```json
{
  "version": 1,
  "segments": [
    { "kind": "intro", "startMs": 62000, "endMs": 151500 },
    { "kind": "outro", "start": "22:10", "end": "23:40", "confidence": 0.95 }
  ]
}
```

**Per folder** – `segments.json` in the media folder or the anime's top-level
folder, keyed by the local season and episode numbers:

```json
{
  "version": 1,
  "episodes": [
    { "season": 1, "episode": 3, "segments": [ { "kind": "op", "start": "1:02", "end": "2:31.5" } ] }
  ]
}
```

Rules:

- `kind` accepts `intro`/`op`/`opening`, `recap`, `outro`/`ed`/`ending`,
  `preview`/`next`, `credits`/`other`.
- Times are `startMs`/`endMs` (integers) or `start`/`end` timecodes
  (`m:ss`, `h:mm:ss`, optional fraction, or plain seconds).
- `confidence` defaults to 1.0 and is clamped to 0–1.
- `season` defaults to 1.
- Lookup order per episode: the episode sidecar wins completely, then
  `segments.json` in the media folder, then in the anime folder.
- Invalid entries are skipped with a warning in the scan log. A file that is
  larger than 1 MiB, not valid JSON, has an unsupported `version` or cannot be
  read (for example an offline share) is ignored and the previously imported
  markers are kept.

## Automatic detection hook

`IMediaSegmentDetector` is the extension point for automatic detection. Results
are stored as `detector` markers with the detector's method, version, confidence
and the media identity they were computed against. Detection is skipped while
media identity and detector version are unchanged; **Re-run segment detection**
forces a rebuild. Detector markers never outrank manual, imported or provider
markers, and a detector must not modify source media.

The default detector is a no-op (`method` `none`). Setting
`MediaSegments:FingerprintDetectionEnabled` to `true` (env var
`MediaSegments__FingerprintDetectionEnabled`) switches to
`AudioFingerprintMediaSegmentDetector`, which finds OP/ED material repeated
across episodes of the same anime and season. It is **off by default**: the
analysis decodes and hashes several minutes of audio per episode and is CPU
heavy, so an owner opts in deliberately rather than it running unattended.

### Cross-episode audio fingerprinting

For each episode, `ffmpeg` decodes the first ~6 minutes ("intro window") and
last ~6 minutes ("outro window") of audio to mono 16 kHz-class PCM (8 kHz,
16-bit). No video is decoded (`-vn`) and nothing is written back to the source
file. The PCM is hashed in C# into one compact fingerprint per ~256 ms analysis
frame (50% overlap), using a Haitsma/Kalker-style robust hash: 33 log-spaced
frequency bands per frame, one bit per pair of adjacent bands set when their
energy difference increased since the previous frame. The hash is invariant to
overall loudness and stable across light re-encoding, which is what makes the
same OP/ED audio comparable when muxed differently across episode files.

**Why C# hashing instead of `ffmpeg -af chromaprint`:** the runtime image
(`debian:bookworm-slim` + apt `ffmpeg`, see `src/Jularr.Web/Dockerfile`) is
not guaranteed to have been built with `--enable-chromaprint` — Debian's
packaged `ffmpeg` does not consistently ship that filter, and confirming it
would require probing the image every time it is rebuilt. Hashing bounded PCM
in C# has no such dependency, keeps the whole pipeline inside the existing
`ffmpeg`/`ffprobe` + .NET toolchain already required for the rest of the
feature, and needs no new native dependency in the Dockerfile.

Given an episode and its analysed siblings (same anime, same season, capped at
24 to bound the pairwise comparison cost), the detector aligns the requested
episode's fingerprint against each sibling's by scanning every possible frame
offset for the longest run of frames agreeing within a small Hamming distance
(`AudioFingerprint.FindBestMatch`). Matching runs from different siblings that
overlap are grouped; a **single corroborating sibling is enough** to report a
marker (2 episodes total), and a **second corroborating sibling raises
confidence** further (3+ episodes total, the preferred minimum). A candidate
segment is rejected outright — not truncated — when it is shorter than ~20 s or
longer than ~3 minutes, since neither bound is a plausible OP/ED length.

**Fingerprint cache**: each episode's intro/outro hash sequences are cached
under `/data/media-segment-cache/fingerprints/<media file>-<identity>-v<version>.json`,
keyed by the canonical media identity and the detector's `FingerprintVersion`
(see [media inventory](PLAYBACK.md#media-inventory) — the detector never
probes on its own). A season re-run after adding one new episode only decodes
and hashes that episode; every sibling's fingerprint is reused from cache. A
new identity or detector version prunes the superseded cache file for that
media file, the same disposable-cache contract as trickplay.

**Per-episode run state**: because a legitimate detection outcome can be "no
shared segment found" (no markers to store), skip-if-unchanged cannot rely on
detector marker rows alone. `EpisodeSegmentDetectionState` (one row per
episode) records the method/version/media identity of the last run regardless
of outcome, so an unchanged episode is never re-analysed by a later season run
until its media identity or the detector version changes, or the owner forces
a rebuild.

**Season detection**: the owner segments page's **Detect intro/outro for this
season** button queues one bounded background Operation (**Admin →
Operations**, kind `segment-detection`, maintenance lane,
`SeasonSegmentDetectionQueue`, at most two seasons analysed at once) that runs
every episode of the season through the same per-episode detector call used by
**Re-run segment detection**. It never blocks the request; a season already
queued is not queued twice. There is currently no automatic trigger after a
library scan — only the owner-initiated season button — since wiring that
requires touching the library scanner, which is out of scope for this change.

**Limits**:
- Recap and preview-card detection are not attempted: unlike a fixed-window
  OP/ED, recaps vary too much in placement and length for a bounded
  intro/outro window to reliably bound, and are left for a future, differently
  shaped detector.
- Only audio is fingerprinted; a purely visual OP/ED (rare, but not unheard of)
  is not detected.
- Very short episodes (intro/outro window shorter than the ~20 s minimum
  segment length) are skipped for that window rather than guessed.
- Cross-episode alignment is quadratic in the number of siblings compared per
  call (bounded to 24); a much longer-running show still analyses in bounded
  time because fingerprints are cached and reused across the season.

## Seek previews (trickplay)

When a player loads a playable episode, Jularr queues a background operation
(**Admin → Operations**, kind `trickplay-generation`, maintenance lane) that
extracts keyframe thumbnails with `ffmpeg` into JPEG sprite sheets. Playback
never waits for it.

- **Identity and duration** come from the canonical [media inventory](PLAYBACK.md#media-inventory):
  the analysed content fingerprint (fallback: size + modification time) and the
  analysed duration. Without a successful analysis there are no previews; the
  generator never probes on its own.
- **Cache**: `/data/playback-cache/trickplay/<media file>-<identity>-v<generator version>/`
  with `index.json` and `sprite-NNN.jpg`. A new identity or generator version
  produces a new directory and prunes the superseded ones of the same media file.
  The cache can be deleted at any time and is regenerated on demand.
- **Bounds**: one thumbnail every 10 seconds or more, at most 720 thumbnails
  (320×180 tiles, 10×10 per sprite), at most two pending generations, a
  20-minute ffmpeg timeout.
- **Degradation**: when ffmpeg or the media file is unavailable the operation
  fails with a readable message, the descriptor reports `unavailable`, and the
  player simply shows no preview. A failed generation is retried after a
  restart, when the media changes, or via **Rebuild seek previews**.

## Client descriptor

The player bootstrap (`GET /api/client/v1/episodes/{id}/player`) and the web
Episode page expose the same additive fields; the capability flags
`mediaSegments` and `trickplay` announce them.

```json
{
  "segments": {
    "skipConfidenceThreshold": 0.8,
    "segments": [
      { "kind": "intro", "startMs": 62000, "endMs": 151500, "source": "imported",
        "method": "sidecar", "version": "1", "confidence": 1.0, "canSkip": true }
    ]
  },
  "trickplay": {
    "state": "ready",
    "message": null,
    "generatorVersion": 1,
    "intervalMs": 10000,
    "tileWidth": 320,
    "tileHeight": 180,
    "columns": 10,
    "rows": 10,
    "thumbnailCount": 142,
    "spriteUrls": [ "/api/client/v1/episodes/{id}/trickplay/sprite-001.jpg?v=3f2a9c0d1e4b5a67-1" ],
    "descriptorUrl": "/api/client/v1/episodes/{id}/trickplay"
  }
}
```

`state` is `ready`, `generating` or `unavailable`. Thumbnail `i` covers
`[i × intervalMs, (i + 1) × intervalMs)` and sits on sprite
`spriteUrls[i / (columns × rows)]` at column `i % columns`, row
`(i / columns) % rows`. Clients poll `descriptorUrl` while the state is
`generating`, and use sprite URLs exactly as given: their `v` query changes with
the media identity and generator version, so privately cached images of a
replaced file are never reused.

Separate endpoints:

```text
GET /api/client/v1/episodes/{id}/segments
GET /api/client/v1/episodes/{id}/trickplay
GET /api/client/v1/episodes/{id}/trickplay/{index.json|sprite-NNN.jpg}
```

## Not included

- Video-based fingerprinting and recap/preview-card detection (see the
  cross-episode audio fingerprinting limits above)
- An automatic trigger for season detection after a library scan (owner
  button only; see above)
- Provider/metadata marker import
- Automatic skipping or per-profile auto-skip preferences
- Viewing analytics or behaviour-based detection
