# Dokumentations-Audit – 11.10.2026

- **Gesicherte Quelle:** [`backup/docs-specs-pre-audit-20261011`](https://github.com/Juloc/Jularr/tree/backup/docs-specs-pre-audit-20261011), Stand `07e4c88a6a0368f627e0026ec8f7663dda5212ba` aus `dev`.
- **Audit-Branch:** `docs/documentation-audit-20261011` vom selben Commit.
- **[Inventar](MD_INVENTORY.md):** 146 Markdown-Dateien, 74 Specs und 94 Mockup-Bilder vollständig nach Pfad gezählt; Triage mit ausdrücklich begrenzter inhaltlicher Prüftiefe.
- **[Widersprüche](CONFLICTS.md):** belegte Risiken, offene Entscheidungen und empfohlene Folgearbeit.

**Keine Originaldokumente geändert, keine Dateien gelöscht, keine Issues geschlossen, keine SQL-/Runtime-Änderung.** Der Backup-Branch sichert `dev`, nicht den Inhalt der ungemergten PR-Branches; #941/#945/#946 sind als zusätzliche Quellen berücksichtigt.

**Nächste Maßnahmen (noch nicht ausgeführt):** kanonische Regeln gegen Phase-B-Entscheidungen prüfen; einzigartige Inhalte vor Archivierung extrahieren; Page Specs nach einheitlichem kurzen Schema entschlacken; Issues erst nach finaler Architektur aktualisieren. Ein Backup-Branch ist ein Git-Snapshot, kein unabhängiges externes Backup.

## Initial consolidation on this review branch

The outdated top-level `PLANNING-AUDIT.md` and `CROSS-SPEC-CONSISTENCY-AUDIT.md` have been removed from active docs; their *exact full content* is preserved at the immutable backup snapshot above. Their dated findings that may still matter are: one consumer Request lifecycle (Instant Play is a playback intent), separate Admin operations versus user-facing status, mobile navigation and distinct authorization/scope constraints, and unresolved approval gates for Calendar and the PlayStation game player. These are **historical findings**; newer per-feature contracts and explicit owner decisions take precedence. Do not treat the old 03.10.2026 “all consistent” conclusion as an up-to-date validation.

The separate `docs/mockups/home/README.md` was also removed: the authoritative Home/Discover `SPEC.md` already contains the theme, hero, visual, fallback, navigation and responsive rules. Full older text and image references remain in the backup snapshot. All 74 page/flow `SPEC.md` files and their images **remain present** until safe, feature-complete replacements exist. Active links to deleted audits were redirected to the historical snapshot or to the current owner.

Additional targeted corrections: database Service-vs-Logic ownership, old Games identity and backfill guidance in `ARCHITECTURE.md`, product roadmap vs cutover tracker and the self-reference in Home. These are draft-branch edits, not changes on `dev`.

## First Page-Spec deduplication

`docs/mockups/discover/SPEC.md` no longer duplicates the shared Home/Discover block. **Fourteen subsections were checked for exact equality** against Home before replacement with a link (approximately 10 KiB of repeated prose). Both screen-specific specs remain; Games wording in both has been reconciled with canonical `Works.Id`. This is not a redesign or a completed review of the other 72 specs.
