# Phase B — statischer SQL-Entwurfscheck (ohne PostgreSQL-Ausführung)

**Stand 10.10.2026:** neun aufeinander aufbauende Entwurfsdateien 01–09 auf Branch `docs/cutoff-phase-b-schema-20261010`, **nicht** in eine laufende DB eingespielt. Dieser Prüfstand ist textbasiert und ersetzt **keinen** PostgreSQL-, EF-, Sicherheits- oder Query-Performance-Test.

| Statische Prüfung | Ergebnis |
| --- | ---: |
| Neue `CREATE TABLE`-Entwürfe | **162** |
| Erkannte Spaltendeklarationen (inkl. UUID/double precision) | **772** |
| FK-Deklarationen einschließlich zusammengesetzter FK | **235** |
| FK-Zieltabellen und Zielspalten vorhanden | Keine fehlenden Namen |
| FK-Zielschlüssel PK/UNIQUE, einschließlich `ALTER TABLE ADD UNIQUE` | Kein fehlender Schlüssel erkannt |
| Doppelte Tabellennamen | 0 |
| Doppelte benannte Constraints | 0 |
| Doppelte benannte Indizes | 0 |

**Dateiumfang:** 01 Core 45, 02 Progress/Images/Operations 36, 03 Media/Music 10, 04 Monitoring/Notifications 9, 05 Learning Curriculum 18, 06 Offline/Provider 5, 07 Acquisition 17, 08 Learning Cards 13, 09 Learning Activity/Gamification 9.

**Gezielte Verbesserungen seit vorherigem Audit:** `MediaAssets(Id,WorkId)` verknüpft die kanonische Work mit ihrer Version; `StoredFiles(Id,WorkVersionId)` und `GameReleaseStoredFiles(StoredFileId,WorkVersionId)` verhindern Game-Dateien fremder Releases; `PlaybackSessions` und `MediaPlaybackHistory` verwenden Work-/Asset-/File-Composite-FKs. `LearningUnitKindTypes` ersetzt freien `KindKey`. `LearnerCourses(Id,ProfileId)` bindet `LearningActivitySessions` profilrichtig. Learning Activity/XP/TimeSlices/DailyGoal/Achievement sind aus `LEARNING_GAMIFICATION.md` abgeleitet, nicht aus einem zweiten globalen Eventbus.

**Neue strukturbezogene SQL-Testvorlage:** [DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql](DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql) prüft `pg_constraint`, DEFERRABLE Progress/Profile-Zyklen, `citext` und `NULLS NOT DISTINCT` nach einer **isolierten** Neuinstallation. Diese Abfrage wurde **nicht ausgeführt**.

**Bekannte fachliche/technische Einschränkungen:** Ein FK auf die richtige WorkVersion erzwingt **noch nicht**, dass diese WorkVersion zu einem `MediaType=game` gehört. Curriculum-Lesson/Exercise und Profil-Blueprint-Zugehörigkeit sind noch nicht vollständig mit Composite-FKs nachgewiesen. Unbestätigte Enum-Seed-IDs, AI-Features, Notification-Sink-Verfügbarkeit und Provider-Policy bleiben in [DECISIONS](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) ausdrücklich offen.

**Pflicht vor Gate B:** Alle 01–09 Skripte auf neu aufgesetzter isolierter PostgreSQL-Zielversion ausführen; positive und negative Integritäts-/Rollback-Tests (Game/Work, Profil/Auth, Offline-Replay, Progress-Subtype, Images, Learning) durchführen; `enum : byte` mit Seed-Manifest abgleichen; WorkCard/Continue/Acquisition/Learning-Queries mit `EXPLAIN (ANALYZE, BUFFERS)` prüfen; fehlende Fachmodelle ergänzen. Eine erfolgreiche statische Prüfung ist **kein Gate-B-Pass**.
