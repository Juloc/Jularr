# Phase B — statischer SQL-Entwurfscheck (ohne PostgreSQL-Ausführung)

**Stand 10.10.2026:** zehn aufeinander aufbauende Entwurfsdateien 01–10 auf Branch `docs/cutoff-phase-b-schema-20261010`, **nicht** in eine laufende DB eingespielt. Dieser Prüfstand ist textbasiert und ersetzt **keinen** PostgreSQL-, EF-, Sicherheits- oder Query-Performance-Test.

| Statische Prüfung | Ergebnis |
| --- | ---: |
| Neue `CREATE TABLE`-Entwürfe | **162** |
| Erkannte Spaltendeklarationen (inkl. UUID/double precision und nachträglicher Discriminator-Spalten) | **782** |
| FK-Deklarationen einschließlich zusammengesetzter FK | **251** |
| FK-Zieltabellen und Zielspalten vorhanden | Keine fehlenden Namen |
| FK-Zielschlüssel PK/UNIQUE, einschließlich `ALTER TABLE ADD UNIQUE` | Kein fehlender Schlüssel erkannt |
| Doppelte Tabellennamen | 0 |
| Doppelte benannte Constraints | 0 |
| Doppelte benannte Indizes | 0 |

**Dateiumfang:** 01 Core 45, 02 Progress/Images/Operations 36, 03 Media/Music 10, 04 Monitoring/Notifications 9, 05 Learning Curriculum 18, 06 Offline/Provider 5, 07 Acquisition 17, 08 Learning Cards 13, 09 Learning Activity/Gamification 9; 10 Curriculum Blueprint-Scope 0 neue Tabellen, 8 FK-Discriminator-Spalten und 15 zusätzliche Composite-FKs.

**Gezielte Verbesserungen seit vorherigem Audit:** `MediaAssets(Id,WorkId)` verknüpft die kanonische Work mit ihrer Version; `StoredFiles(Id,WorkVersionId)` und `GameReleaseStoredFiles(StoredFileId,WorkVersionId)` verhindern Game-Dateien fremder Releases; `PlaybackSessions` und `MediaPlaybackHistory` verwenden Work-/Asset-/File-Composite-FKs. `LearningUnitKindTypes` ersetzt freien `KindKey`. `LearnerCourses(Id,ProfileId)` bindet `LearningActivitySessions` profilrichtig. Learning Activity/XP/TimeSlices/DailyGoal/Achievement sind aus `LEARNING_GAMIFICATION.md` abgeleitet, nicht aus einem zweiten globalen Eventbus.

**Neue strukturbezogene SQL-Testvorlage:** [DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql](DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql) prüft `pg_constraint`, DEFERRABLE Progress/Profile-Zyklen, `citext` und `NULLS NOT DISTINCT` nach einer **isolierten** Neuinstallation. Die strukturbezogenen Katalog-Assertions wurden im isolierten [PostgreSQL-18-CI-Lauf](https://github.com/Juloc/Jularr/actions/runs/38073509274) erfolgreich ausgeführt. Die später ergänzten gezielten positiven/negativen DB-Tests müssen getrennt nachgewiesen werden.

**Bekannte fachliche/technische Einschränkungen:** Ein FK auf die richtige WorkVersion erzwingt **noch nicht**, dass diese WorkVersion zu einem `MediaType=game` gehört. Curriculum-Lesson/Exercise/Profil-Blueprint-Zugehörigkeit wird nun in Part 10 zusätzlich mit Composite-FKs abgesichert. Negative Datenintegritätstests und Lern-Publikations-/Profile-Rechtesemantik bleiben abzunehmen. Unbestätigte Enum-Seed-IDs, AI-Features, Notification-Sink-Verfügbarkeit und Provider-Policy bleiben in [DECISIONS](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) ausdrücklich offen.

**Pflicht vor Gate B:** Alle 01–10 Skripte auf neu aufgesetzter isolierter PostgreSQL-Zielversion ausführen; positive und negative Integritäts-/Rollback-Tests (Game/Work, Profil/Auth, Offline-Replay, Progress-Subtype, Images, Learning) durchführen; `enum : byte` mit Seed-Manifest abgleichen; WorkCard/Continue/Acquisition/Learning-Queries mit `EXPLAIN (ANALYZE, BUFFERS)` prüfen; fehlende Fachmodelle ergänzen. Eine erfolgreiche statische Prüfung ist **kein Gate-B-Pass**.

**Realer Bootstrap-Nachweis:** Im [GitHub Actions Scratch-DB-Lauf](https://github.com/Juloc/Jularr/actions/runs/38073509274) wurden alle zehn DDL-Abschnitte und die pg_catalog-Assertions erfolgreich ausgeführt. Davor gab es nur statische Checks. Die 162 Tabellen sind Zielentwürfe, die 782 Spalten enthalten acht Blueprint-Discriminator-Spalten und zwei zusätzliche GameRelease-Spalten; weitere Fach-/Seed-/EF-/Performance-Gates bleiben offen.

**Echte Integritäts-Negativtests bestanden (CI):** [PostgreSQL-18-Testlauf #38073814369](https://github.com/Juloc/Jularr/actions/runs/38073814369) hat den vollständigen 01–10-DDL-Aufbau, die Katalogassertions und **acht** positive/erwartet-negative Szenarien erfolgreich ausgeführt. Abgedeckt: Blueprint-Fremdbezüge in LearnerAttempts/Progress/SharedCourseContent/Activity, MediaProgress-Subtype beim Commit, fremde Profile in AccountSessions sowie ImageAssignment-Cardinality und ImageTargetKind. Tests arbeiten ausschließlich mit temporären Fixtures und `ROLLBACK`. Für andere fachliche Lücken, die vollständige C#-Service-/Logic-Authorisierung, EF-Snapshot, Replay-Races, Index-EXPLAIN und Seeder-Freigabe gilt weiterhin **Gate B offen**.

**Zusätzliche CI-Abnahme der echten Cross-Work-/Datei-Constraints:** [PostgreSQL 18 Scratch Run #38074610636](https://github.com/Juloc/Jularr/actions/runs/38074610636) **erfolgreich**. Die Tests wurden von zuvor 8 Negativfällen um **11 gezielte Rejection-Szenarien** plus positive Kontrollfälle ergänzt. Enthalten sind Profile-Owner-Link, Assets und Files über Work/Version, GameRelease-File-Membership, Playback Asset/File Work-Zuordnung, Progress Episode/NULLS-Duplikat, Segmentdauer, Detection NoMatch und Wanted-Unique-/Episode-Membership. CI-Resultate ersetzen **nicht** offenen B01-Enum-Seed-Go, B06 Game-only-Seed-Freigabe, Provider-/Auth-/Acquisition-/Notification-Policy, Last-/EXPLAIN oder Phase-C-EF-Migration.

**T07 wirklich PostgreSQL-getestet:** [Scratch PG18 Run #38074769345](https://github.com/Juloc/Jularr/actions/runs/38074769345) ist erfolgreich. Zwei neue erwartete FK-Ablehnungen bestätigen, dass eine Film-`WorkVersion` nicht in `GameReleases` eingefügt werden kann und dass eine Game-WorkId nicht eine andere (Film-)Version annehmen kann. Die gültige Game-WorkVersion ist der Positivfall. Der Discriminator `GameMediaTypeId=7` ist im DRAFT noch eine **provisorische** Zahl: vor Gate B/Baseline an B01-C#-Enum/Seed-Vertrag binden.
