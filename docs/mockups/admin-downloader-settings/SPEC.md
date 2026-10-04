# Admin Downloader Settings — V1

Status: approved planning direction; current Einstellungen mockup is the visual baseline.

Shared contract: `docs/mockups/admin-downloader/SPEC.md`.

## Purpose

Configure general native downloader behavior that does not belong to Servers, Processing, Speed/Schedule or Storage.

## Sections

Recommended tabs:
- Allgemein
- Verhalten
- Duplikate
- Speicher-Schutz
- Aufbewahrung
- Erweitert

Do not make one enormous form.

## Allgemein

Settings:
- start paused
- default priority
- queue persistence
- crash recovery
- automatic recovery after application restart
- default Native Download Workspace selection from Storage
- optional Repair/Extract Workspace selection
- native downloader enabled state

Workspace selectors reference Storage roles only.

The native downloader enabled state is a Downloader service setting (for choosing whether Jularr's built-in transport may accept work), **not** an `InstanceModule` switch. It must not create a fake module gate or hide unrelated Acquisition/Downloader compatibility configuration.

## Retry / failure behavior

Configure:
- standard retry count
- retry delay/backoff
- retry server failover
- behavior after repeated failure
- pre-check policy
- automatic retry after temporary server/storage outage

Do not endlessly retry hard failures.

## Duplicate handling

Policies may include:
- reject exact duplicate NZB/release
- warn only
- allow explicit manual duplicate
- detect already completed/failed history
- detect same acquisition target already in queue
- optional later-stage content hash/fingerprint evidence during import

Show the exact duplicate reason when blocked.

## Low disk / workspace protection

Configure:
- minimum free workspace
- warning threshold
- auto-pause threshold
- optional auto-resume threshold
- completed-temp retention pressure behavior

Storage remains authoritative for actual capacity.

## Retention

Configure independently:
- completed downloader history retention
- failed history retention
- NZB metadata retention
- temporary completed/staging retention
- downloader telemetry sample retention where useful

Retention must never delete imported canonical library media.

## Notifications

Downloader-specific event toggles may exist only if global notification infrastructure exists:
- job failed
- server unavailable
- workspace low
- password required
- repeated repair failure

Destination/channel configuration remains global Admin settings.

## Advanced

Collapsed by default:
- article cache size
- write buffer/direct-write policy
- assembler tuning
- global internal timeout defaults
- telemetry sampling
- diagnostic verbosity reference

Dangerous options require explanatory text and validation.

## Reset / diagnostics

May support:
- reset section to defaults
- export sanitized diagnostic snapshot

Do not export secrets.

## States

Additional:
- invalid workspace reference
- retention conflict
- low-space thresholds invalid
- restart required
- invalid advanced combination

## Must not implement

- No physical path text fields bypassing Storage.
- No NNTP server editor here.
- No verify/extract options duplicated here.
- No global notification destination editor here.
- No secret-bearing config export.
- No raw config/YAML as the primary editor.
