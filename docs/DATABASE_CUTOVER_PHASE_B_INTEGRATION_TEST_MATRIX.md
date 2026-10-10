# Phase B/C/D – PostgreSQL-Invarianten und Funktionsabnahmetests

**Status: Prüfanweisung, NICHT ausgeführt.** Die zehn DDL-Dateien aus [PR #946](https://github.com/Juloc/Jularr/pull/946) sind nicht die produktive Baseline. Diese Tests dürfen **nur auf einer eigens isolierten, wegwerfbaren PostgreSQL-Datenbank** mit festgehaltenem Schema-/C#-Stand laufen. Kein Eingriff in `dev` oder lokale User-/NAS-Daten. Die strukturellen [pg_catalog-Assertions](DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql) prüfen die Anwesenheit einiger Constraints; sie sind **nicht** gleichbedeutend mit diesen Laufzeit-/Negativtests.

| Nr. | Gezielter Test | Erwartete Garantie / Besitzer |
| --- | --- | --- |
| T01 | Frisch DB, DDL 01→10 in Reihenfolge, zweite frische DB identisch; PostgreSQL `pg_catalog`, Id/PK/UK/FK/Seed überprüfen | Alle B-DDL-Dateien und Enum Seeds reproduzierbar; Phase B/C |
| T02 | Ein `Profile` ohne `AccountProfiles`-Owner-Membership committen; anschließend Profile+Membership zusammen in EINER Tx | Ohne Owner-Link Commit-Fehler; zusammen erfolgreich (deferrable FK); B + Logic |
| T03 | `AccountSessions(AccountId,ActiveProfileId)` auf Profil einer fremden AccountMembership setzen; Profil-Transfer unter konkurrierender Session-Aktivität | Composite FK/Service-Recheck verhindert unzulässige Profilnutzung; B + D |
| T04 | `MediaAssets(WorkVersionId,WorkId)` mit fremder WorkId schreiben | Composite FK verhindert Cross-Work-Asset; B |
| T05 | `StoredFiles(MediaAssetId,WorkVersionId)` mit fremder WorkVersionId schreiben | Composite FK verhindert fremdes File↔Version; B |
| T06 | `GameReleaseStoredFiles` auf ein File einer anderen WorkVersion setzen; gültige Multi-Disc-Files eintragen | Fremdes File abgelehnt, gültige Reihenfolge erhalten; B |
| T07 | Ein GameRelease auf WorkVersion eines `movie` Work setzen | Muss nach finaler B01-MediaType-Entscheidung in DB oder mit transaktionaler Logic+Lock/Race-Test abgelehnt werden; **noch OFFEN**, B06 |
| T08 | PlaybackSession mit `StoredFileId` einer anderen MediaAssetId, oder MediaAssetId eines anderen WorkId; PlaybackHistory Asset/Work mismatched | Fremdfile/-asset abgelehnt, richtige Workzuordnung; B |
| T09 | `MediaProgress` Time-Typ ohne `TimeProgressPositions` committen; mit ReadingPosition statt TimePosition; mehrere Positiontypen | Commit fehlgeschlagen; genau passender Detailtyp pro Progress, keine versteckte Trigger-Businesslogik; B |
| T10 | Progress mit Episode/Chapter/Edition eines anderen WorkId, konkurrierende Revision, doppeltes Offline-`ClientEventId` | Cross-Work FK, UNIQUE+Revision-Logik, idempotente Replay-Ablehnung; FK B, Logic D/E |
| T11 | Zwei `MediaProgress` für denselben Profile/Work/NULL-exact Target | `UNIQUE NULLS NOT DISTINCT` verhindert Duplikate; B |
| T12 | Ungültige ImageAssignments: kein Ziel, mehrere Ziele, falscher ImageType/TargetKind; manuelles Override mit Fallback | CHECK/FK verhindern ungültige Targets, Sort-/Locale-Regeln in Read-Service D/E |
| T13 | `MediaSegments` ungültige End/Start, wiederholte/überlappende Intro/Recap/Credits; Detection-Run `MatchCount=0` | Zeit-Checks greifen, legitime Überlappung und NoMatch bleiben möglich; B und D/E |
| T14 | `LearnerCourse` für Profil A, LearningActivitySession mit Profil B; LearningCardReview für fremdes Profil | Composite FKs verhindern fremde Lernzustände; B |
| T15 | Curriculum Exercise auf Objective einer **anderen** Lesson; LearnerCourse Attempts/Progress zu fremdem Blueprint | Cross-Lesson-FK verhindert ersteres; Part 10 fügt Composite-FKs für gepinnten Blueprint hinzu; echte Negativ-/Race-/Publish-Tests bleiben **B05 Pflicht** |
| T16 | Doppelte LearningActivitySession(`ProfileId,ClientSessionId`), gleiches LearningActivityEvent(`ProfileId,SourceEventId,Kind`) | Eindeutigkeitsverletzung, keine doppelte XP-Vergabe; B |
| T17 | Client-Monotonic-ActiveMilliseconds gleich oder niedriger; idle/background; excessive elapsed; Learning-Gamification Off | Kein doppelt gezählter Sekundenwert, kein XP Off, echte Lern-Interaktionszeit; **Logic D/E** |
| T18 | DailyGoal einmal pro Profil+LocalDate erzeugen, danach Präferenzen ändern, Tag wechseln, deaktivieren/re-aktivieren | Snapshot bleibt stabil, Streak ist abgeleitet, keine retroaktive XP-Vergabe; **Logic D/E** |
| T19 | Achievement zweimal unlocken, Kurszustand bei Achievement-Auswertung-Fehler fortsetzen | UNIQUE unlock; Fehler isoliert, keine doppelte Achievement-Progress-Wahrheit; B und D/E |
| T20 | `AcquisitionRequestTargets`, `WantedItems`, MonitoringDecision mit fremder WorkEpisodeId/WorkId, Duplicate/Inherit | FK/UNIQUE korrekt; Wanted schreibt nur Reconciler, keine zweite Queue; B/D |
| T21 | Operation Queue Claim `SKIP LOCKED` mit zwei Workern, abgestürzter Lease, Force vs Direct, Provider/NAS-Timeout | Keine Doppelvergabe, Lease-/Retry-Verhalten und kurze gemeinsame DB-Transaktion; B/D/E |
| T22 | Plex/Jellyfin LibraryGrant/ProfileConsent, falscher AccountOwner, externe Credentials/Provider offline | Kein fremder Providerzugriff oder Secret im DB-Log, keine ungewollte Watchlist/Progress-Importmutation; B/D/E |
| T23 | User/Admin WorkCard mit >10 Episoden/Tracks, Locale-Image-Fallback, 100 PageSize, mehrere Profile | Ein paginierter Root-SELECT, bounded LATERAL-Kinder, korrektes Profilfilter und sortierte Page, kein N+1; SQL/EXPLAIN B+D |
| T24 | Notification Events an Profile/Admin, Preferences für nicht vorhandenen Sink, Zustellungsfehler, Retry | Sink-Verfügbarkeit an Runtime gebunden; kein fiktiver Push/Email-Erfolg, gebrochener Sink stoppt nicht Medienmutation; B/D/E |

**Sicherheitsregel:** Bei negativer PostgreSQL-Ausführung die erwartete Fehlermeldung nach Klasse `23503` (FK), `23505` (UNIQUE) oder `23514` (CHECK) gezielt prüfen. `ROLLBACK TO SAVEPOINT` innerhalb einer temporären Testtransaktion benutzen. **Nie** eine fehlgeschlagene Datenbankschema-Bootstrap-Runde durch `CREATE TABLE IF NOT EXISTS` oder andere stille DDL-Fallbacks „reparieren“.

**Noch offen:** B01 vollständiges `enum : byte`-Seed-Manifest; T07 Game-only-WorkType-Garantie; T15 Pinned-Blueprint-Negativtests/Source-Konsistenz; Learning-ActiveTime/Session-/Locale-/TimeZone-Contract; NotificationDelivery-/Outbox-Schema vs sink runtime; Provider Auth- und Acquisition-Rules. Diese bleiben [explizite Phase-B-Entscheidungen](DATABASE_CUTOVER_PHASE_B_DECISIONS.md), nicht als bestanden markieren. Erst die B/C/D-Evidence mit echtem PG-/EF-/Consumer-Code erlaubt eine spätere Gate-Freigabe.

**Automatisierte Teilprüfung:** [DDL-/Katalog-CI](https://github.com/Juloc/Jularr/actions/runs/38073509274) hat die isolierte PostgreSQL-18-Neuinstallation bestanden. [Gezielte Negativtest-SQL](DATABASE_CUTOVER_PHASE_B_PG_NEGATIVE_TESTS.sql) wird in derselben Scratch-DB-CI geprüft; die übrigen 24 End-to-End-/Service-/Race-Tests sind ausdrücklich noch nicht abgeschlossen.

**Verifiziert (CI, nicht alle 24 Tests):** [Erfolgreicher isolierter PostgreSQL-18-Run](https://github.com/Juloc/Jularr/actions/runs/38073814369) beinhaltet acht fokussierte SQL-Szenarien zu **T03** (AccountSession fremdes Profil), **T09** (Progress-Subtype, teilweise), **T12** (Images, teilweise) und **T15** (Cross-Blueprint Attempts, CourseProgress, SharedCourseContent, ActivitySessions). Die übrigen Aspekte derselben Tests und alle nicht genannten T-Nummern stehen weiterhin aus.
