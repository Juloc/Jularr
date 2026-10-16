# Phase B — PostgreSQL-Abnahme ohne Learning

Nur isolierte, frische PostgreSQL-18-Testdatenbanken. Keine dev-/Produktiv-/NAS-
Daten, keine stille Bootstrap-Reparatur durch IF NOT EXISTS oder Fallback-DDL.
Aktueller Umfang und offene Gates: [Schema](DATABASE_CUTOVER_PHASE_B_SCHEMA.md),
[Entscheidungen](DATABASE_CUTOVER_PHASE_B_DECISIONS.md).

| Prüfung | Garantie / Abgrenzung |
| --- | --- |
| Frischer Bootstrap aller vierzehn DDL-Dateien | Vollständiger aktueller Target-Satz, nicht alte Migrationen |
| Katalogassertions | FKs/CHECKs/DEFERRABLE/NULLS NOT DISTINCT; ausdrücklich keine Learning-Tabellen oder Profil-Learning-Spalte |
| Defined Seeds / Type Catalogs | 40 Byte-/smallint-Kataloge, 198 exakt definierte Codes, unbekannte FK-Werte und Bereichs-/Blank-Key-Ablehnung |
| Auth Lifecycle | Hash-/Purpose-/Account-Grenzen, Expiry, Rollback/Replay, Credential-Epoch, TOTP/Recovery, Transfer und zwei parallele Rotation-/Recovery-Sessions |
| Account/Profile/Groups | Owner-Membership, fremde AccountSession-Profile, neutrale Gruppen, eindeutige Namen, RESTRICT und Owner-Paging |
| Provider login/media opt-ins | Vier unabhängige default-off Capability-Felder; statische Login/Link/Media-Zweck-READs, Profilmembership/Disable und Policy-Entzug mit echter PostgreSQL-Testausführung |
| Provider lifecycle | Revisionierte Consent-/Grant-Referenzen, Owner/Sections, Token-freie READs, Operation-Lease-/Generation-/Cursor-CAS, Commit-Accepted-Page, Restart/Rollback und 1000er Paging/Work-Isolation |
| Assets/Files/Game/Playback | Identische Work-/Version-/Asset-/File-Zuordnung; Game-only und Multidisc-FKs |
| Progress/Reader | Atomarer Root plus genau ein Positionstyp, exakte Edition-/Contentrevision/Quellseite, Completion-Provenienz und manuelles Unmark, Hash-Replay/Payload-Konflikt, aktuelles Membership/CAS und gemeinsamer Rollback |
| Reader Content/Annotations | Exakte veröffentlichte Edition/Kapitel/Variante/Revision, drei Locator-Shapes, dichte immutable Indizes, Source-FKs, private Delivery, CAS/Replay/Overlap und 1000er Node-Feed |
| Reader chapter scope | Zwei Kapitel derselben Edition mit Revision 1; NULL-Vollinhalt eindeutig, keine Content-/Page-/Annotation-Fanouts, kein Cross-Chapter-Checkpoint oder direkter Write |
| Music identity/recordings | Ein People-PK für Artist/Credits, Solo/Ensemble, getrennte Public-UUIDs und Provideridentitäten; gemeinsames Recording auf zwei Alben, Credit-Provenienz, Rollback/FK/Unique und autorisiertes 1000er Paging vor Enrichment |
| Music lifecycle/placements | Music-only Album, genauer Release/Disc/Track, NULL-Recording, Cross-Work-/Duplicate-Rejection, mehrfache Crew-Rollen, vier Relationquellen, Off-Priorität, All/None/Future, aktueller Owner/Profile, Retry und Rollback; 1000er Paging vor Detail-Lookups |
| Artist monitoring concurrency | Drei nachweislich überlappende Sessions: ein Future-Command, exakter Retry-Beleg, spätere Album-Veröffentlichung unter Artist-Mutex; spätes Album erbt Follow, Track-Entscheidung bleibt erhalten |
| Reader publication concurrency | Drei echte PG-Sessions: genau ein Publisher, kein verspäteter Index-Write oder halbfertiger veröffentlichter Inhalt |
| Progress concurrency/completion | Zwei unterschiedliche berechtigte Accounts: genau ein Root-/Checkpoint-Gewinner, keine stale Position/Completion/Belege; lückenloser SQL-Episoden-Präfix mit Specials/Season-Grenzen und 1000 gleich datierten Continue-Roots |
| Images/Segments/Detection | Genau ein typisiertes Bildziel, valide Zeitspannen, legitime Überlappung, No-Match und wiederholte Detection-Runs |
| Wanted/Acquisition | Exact-Target-Unique, Cross-Work-Ablehnung, Pack-Coverage und Download-Bindings |
| Events/Notifications | Profile-/Admin-Audience, Category-Policy, Recurrence/Replay/Dismissal, Commit-Zählerintegrität und zwei parallele Gruppierungs-Sessions |
| Zyklische Lifecycles | Pflicht-Owner und alle drei Progress-Subtypen: Orphans verboten, explizite Deletes im selben Tx erlaubt, keine Cascade |
| Notification Lifecycle | Cross-Midnight/DST, Critical-/In-App-Bypass, Digest-Routes/Disable-Auflösung, kein Leer-/Doppeltiming, Endpoint-/Session-/Email-/Rollen-Revoke |
| Watchlist/Continue/Groups/Inbox/Reader Reads | Statische typisierte PREPAREs, Autorisierung vor Root-Paging, deterministische Seiten, Membership-Entzug; Reader Bookmarks/Highlights/Pages mit Work-/Revisions-Isolation, Page-Limits und EXPLAIN |
| Worker Claims | Zwei reale PostgreSQL-Sessions, SKIP LOCKED, Retry-Fälligkeit und keine Doppelvergabe |
| AcquisitionRequest history | Profilautorisiertes SQL-Paging, öffentliche Request/Work/Operation-IDs, genau begrenzte Ergebnisse, Sharing/Entzug/Disable, EXPLAIN (ANALYZE, BUFFERS) |
| Öffentliche IDs | Separate UUID-Adresse, interne bigint-FKs; PublicId gewährt keine Berechtigung |

Negative Fälle prüfen erwartete SQLSTATE-/Constraint-Bedeutung; Fixtures sind
ausschließlich wegwerfbare Testdaten (teils Commit für Paralleltests). Strukturelle Assertions allein beweisen keine Service-Autorisierung oder
Produktregeln. Aktuelle CI muss den geänderten PR-Head ausführen.

Learning-/Curriculum-/FSRS-/Gamification-Tests sind aus diesem Gate entfernt.
Wiedereinführung gehört zum [späteren Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md),
nicht zu reservierten Testfixtures der Baseline.

Noch offen sind die vollständigen in-scope Fachverträge, weiteren statischen
Search-/Playable-/Arr-/Jobs-/Admin-Queries und repräsentativen
Leistungs-/Isolationstests. EF gehört in C, Runtime/Consumer in D/E und
vollständige Release-/E2E-Abnahme in F. Kein vorzeitiges Gate-B-GO.
