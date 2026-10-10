# Phase B — PostgreSQL-Abnahme ohne Learning

Nur isolierte, frische PostgreSQL-18-Testdatenbanken. Keine dev-/Produktiv-/NAS-
Daten, keine stille Bootstrap-Reparatur durch IF NOT EXISTS oder Fallback-DDL.
Aktueller Umfang und offene Gates: [Schema](DATABASE_CUTOVER_PHASE_B_SCHEMA.md),
[Entscheidungen](DATABASE_CUTOVER_PHASE_B_DECISIONS.md).

| Prüfung | Garantie / Abgrenzung |
| --- | --- |
| Frischer Bootstrap aller neun DDL-Dateien | Vollständiger aktueller Target-Satz, nicht alte Migrationen |
| Katalogassertions | FKs/CHECKs/DEFERRABLE/NULLS NOT DISTINCT; ausdrücklich keine Learning-Tabellen oder Profil-Learning-Spalte |
| Defined Seeds / Type Catalogs | 28 Byte-/smallint-Kataloge, 145 exakt definierte Codes, unbekannte FK-Werte und Bereichs-/Blank-Key-Ablehnung |
| Account/Profile/Groups | Owner-Membership, fremde AccountSession-Profile, neutrale Gruppen, eindeutige Namen, RESTRICT und Owner-Paging |
| Assets/Files/Game/Playback | Identische Work-/Version-/Asset-/File-Zuordnung; Game-only und Multidisc-FKs |
| Progress/Reader | Genau ein Positionstyp, Reader-/Edition-/Work-Zuordnung, nullable Dimensionspaare, Revision/CAS und Offline-Replay |
| Images/Segments/Detection | Genau ein typisiertes Bildziel, valide Zeitspannen, legitime Überlappung, No-Match und wiederholte Detection-Runs |
| Wanted/Acquisition | Exact-Target-Unique, Cross-Work-Ablehnung, Pack-Coverage und Download-Bindings |
| Events/Notifications | Profile-/Admin-Audience, Category-Policy, Recurrence/Replay/Dismissal, Commit-Zählerintegrität und zwei parallele Gruppierungs-Sessions |
| Zyklische Lifecycles | Pflicht-Owner und alle drei Progress-Subtypen: Orphans verboten, explizite Deletes im selben Tx erlaubt, keine Cascade |
| Watchlist/Continue/Groups/Inbox Reads | Statische typisierte PREPAREs, Autorisierung vor Root-Paging, deterministische Seiten und Rollen-/Membership-Entzug |
| Worker Claims | Zwei reale PostgreSQL-Sessions, SKIP LOCKED, Retry-Fälligkeit und keine Doppelvergabe |
| Öffentliche IDs | Separate UUID-Adresse, interne bigint-FKs; PublicId gewährt keine Berechtigung |

Negative Fälle prüfen erwartete SQLSTATE-/Constraint-Bedeutung; Fixtures rollen
zurück. Strukturelle Assertions allein beweisen keine Service-Autorisierung oder
Produktregeln. Aktuelle CI muss den geänderten PR-Head ausführen.

Learning-/Curriculum-/FSRS-/Gamification-Tests sind aus diesem Gate entfernt.
Wiedereinführung gehört zum [späteren Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md),
nicht zu reservierten Testfixtures der Baseline.

Noch offen sind die vollständigen in-scope Fachverträge, weiteren statischen
Auth-/Search-/Reader-/Playable-/Arr-/Jobs-/Admin-Queries und repräsentativen
Leistungs-/Isolationstests. EF gehört in C, Runtime/Consumer in D/E und
vollständige Release-/E2E-Abnahme in F. Kein vorzeitiges Gate-B-GO.
