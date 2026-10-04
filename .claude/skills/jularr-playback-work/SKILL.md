---
name: jularr-playback-work
description: Jularr playback delivery work - ClientCapabilities, PlaybackPlan, Direct Play / Direct Stream (remux) / Transcode, hardware acceleration, buffering, ActiveSession vs MediaProgress, diagnostics, iOS/Safari. Use for any player or streaming change (#403 family).
---

# Jularr playback work

Binding sources: `docs/mockups/player/SPEC.md`, issue #403, `docs/MEDIA_CORE.md`, `docs/implementation/request-to-play-readiness-audit.md`. Extend the canonical owners in `Features/Playback`, `Features/PlaybackSessions` and `Features/Progress`; never add a second player, adaptive engine or progress store.

- **Plan**: the server computes a `PlaybackPlan` through the single `PlaybackDecisionEngine` from client capabilities, container/video/audio codecs, subtitles, bitrate/resolution, selected tracks and server capabilities. Order: Direct Play, then Direct Stream (FFmpeg stream copy, lossless remux, no codec re-encode), then segmented on-demand Transcode (never pre-transcode whole media). The plan must explain why ("Why not Direct Play?").
- **Targets**: Movie -> Work, Anime/TV episode -> WorkEpisode via `PlaybackVideoTarget`; files come from canonical `MediaAsset/StoredFile/MediaTrack`. New code never calls the legacy Anime episode-id overload.
- **Hardware**: detect Quick Sync/VAAPI/NVENC/AMD capability at runtime and never assume it; software fallback must stay usable on modest low-core servers; avoid decode plus re-encode when stream copy suffices.
- **State**: `ActiveSession` owns live playback state and `MediaProgress` owns durable position/history/completion. Resume is not Completed. Opening a session creates no consumption progress. AniList write-back uses completed-through only.
- **Buffering/diagnostics**: configurable buffer, expose buffered range, mode, quality, transcode reason and stalls/rebuffer. Adaptive quality uses throughput, buffer, stalls, variants and server capacity inside the existing plan/session, with no second adaptive architecture. If a policy is not durably specified, update the issue/spec first.
- **iOS/Safari**: one shared player; `<video playsinline>`, Jularr inline controls, explicit presentation state, element fullscreen when supported else Theater fallback, native fullscreen only where needed, capability-driven PiP, no scattered UA checks.
- **Tests**: planner tests per target kind and capability matrix, session/progress separation, idempotent progress updates, FFmpeg argument construction without invoking real FFmpeg where possible.
