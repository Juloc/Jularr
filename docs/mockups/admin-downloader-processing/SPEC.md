# Admin Downloader Processing — V1

Status: approved planning direction; current Verarbeitung mockup is the visual baseline.

Shared contract: `docs/mockups/admin-downloader/SPEC.md`.

## Purpose

Configure the native post-download pipeline from completion of NNTP transfer through verification, repair, extraction and handoff.

This page does not configure final Library naming/routing.

## Sections

Use grouped tabs/sections:
- Verify & Repair
- Extract
- Cleanup
- Post-processing

## Verify & Repair

Configurable:
- quick verification
- full verification conditions
- PAR2 verification
- automatic repair
- maximum repair attempts
- fetch/use additional recovery data where available
- behavior when not repairable
- verification concurrency
- optional pause download while heavy repair runs
- fast-fail/pre-check policy for obviously unrecoverable jobs

Do not mark a job complete before required integrity checks finish.

## Extract

Configurable:
- auto extract
- supported archive formats
- nested archives
- direct/streaming unpack where technically safe
- extraction concurrency
- behavior when extraction fails
- behavior when encrypted/password protected
- password source policy
- optional password list/secret store integration
- pause/limit downloads during heavy extraction if configured

Never log archive passwords.

## Cleanup

After successful phases, configurable cleanup may include:
- source archive parts
- PAR/recovery files
- NZB metadata
- temporary assembly files
- extraction temp
- known unwanted extensions

Cleanup must distinguish:
- temporary downloader data
- final imported content

Never delete canonical library content through cleanup rules.

## Post-processing

Downloader-local post-processing may include:
- normalize filenames needed for handoff
- remove junk files
- prepare manifest
- signal Identify/Import pipeline

Content-specific naming/organization belongs to the canonical importer/library implementation for that content type. Storage owns the target LibraryRoot and placement policy. Acquisition selects what should be acquired; it does not own filesystem naming or library organization.

## Pre-check / direct write / assembler tuning

Advanced options may include:
- article availability pre-check
- direct write where safe
- article cache/buffer
- assembler memory/disk strategy

These are advanced and should not dominate the default page.

## Performance preview

Show estimated impact/warnings for combinations such as:
- multiple parallel repairs on slow disk
- extraction workspace on different filesystem
- insufficient temp space

## States

Additional:
- verifier unavailable
- PAR tool unavailable
- extractor unavailable
- encrypted archive
- password required
- repair possible
- repair impossible
- temp workspace full

## Must not implement

- No arbitrary executable/script field as default post-processing.
- No content-specific import naming rules here.
- No cleanup rule that can silently delete canonical library files.
- No passwords in logs/history.
