# Phase B — statischer SQL-Entwurfscheck (ohne PostgreSQL-Ausführung)

**Stand 10.10.2026:** zehn aufeinander aufbauende Entwurfsdateien 01–10 auf Branch `docs/cutoff-phase-b-schema-20261010`, **nicht** in eine laufende DB eingespielt. Dieser Prüfstand ist textbasiert und ersetzt **keinen** PostgreSQL-, EF-, Sicherheits- oder Query-Performance-Test.

| Statische Prüfung | Ergebnis |
| --- | ---: |
| Neue `CREATE TABLE`-Entwürfe | **162** |
| Erkannte Spaltendeklarationen (inkl. UUID/double precision) | **780** |
| FK-Deklarationen einschließlich zusammengesetzter FK | **250** |
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

**Realer Bootstrap-Nachweis:** Im [GitHub Actions Scratch-DB-Lauf](https://github.com/Juloc/Jularr/actions/runs/38073509274) wurden alle zehn DDL-Abschnitte und die pg_catalog-Assertions erfolgreich ausgeführt. Davor gab es nur statische Checks. Die 162 Tabellen sind Zielentwürfe, die 780 Spalten enthalten acht neue Blueprint-Discriminator-Spalten; weitere Fach-/Seed-/EF-/Performance-Gates bleiben offen.
