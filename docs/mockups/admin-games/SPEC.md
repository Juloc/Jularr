# Admin Games — Shared Contract

Status: shared Admin Games contract. Required non-player Games-specific Admin surfaces are approved.

Shared Games contract: `docs/mockups/games/SPEC.md`.

## Purpose

Admin Games contains only Games-specific operational configuration that cannot live in existing shared Admin areas.

## Planned surfaces

- Runtimes: `admin-games-runtimes/SPEC.md`
- Runtime editor: `admin-games-runtime-editor/SPEC.md`
- BIOS/Firmware: `admin-games-bios/SPEC.md`
- BIOS/Firmware editor: `admin-games-bios-editor/SPEC.md`
- Import resolution: `admin-games-import-resolution/SPEC.md`

## Existing Admin ownership

Do not duplicate:
- Games LibraryRoot -> Admin Storage
- game metadata provider configuration/health -> Admin Providers
- indexers/release-search provider configuration -> Admin Providers; release scoring/policy -> Admin Acquisition Profiles
- native download queue -> Admin Downloader
- global jobs/failures -> Activity / To-Do / History

Game-specific surfaces may deep-link to these areas.

## Isolation rule

Admin runtime configuration may grant only explicit capabilities/resources.

A runtime must never receive broad Jularr/host access merely because it is configured under Games.

## Platforms

Desktop primary.
Tablet/mobile support essential monitoring/configuration where practical.
TV unsupported for Admin.


## Approval status

Approved Games-specific Admin surfaces:
- Runtimes;
- Runtime editor;
- BIOS/Firmware overview;
- BIOS/Firmware Add/Replace;
- ambiguous import resolution.

Shared Admin pages remain authoritative for:
- Games LibraryRoot -> Storage;
- Games metadata-provider configuration/health -> Providers;
- indexers/providers -> Providers; scoring/policy -> Acquisition Profiles; transport/queue -> Downloader;
- cross-system failures -> Activity / To-Do / History.

No additional Games-specific Admin page is currently required for the non-player scope.
