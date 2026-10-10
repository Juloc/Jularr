# Datenbank-Clean-Cut — Phase A abgeschlossen (vereinfachtes fachliches Gate)

**Status: GATE A BESTANDEN nach der vom Owner am 10.10.2026 präzisierten Aufgabenstellung. Phase B (exakte neue DDL) kann starten.** Keine Schemaänderung, kein Datenbankzugriff in dieser Abschlussrunde, kein Reset, keine neuen Service-Implementierungen und keine Feature-PR-Merges. Die bisherige umfangreiche technische Quelleninventur bleibt als Beleg erhalten; **ihre Detailtreffer sind nicht mehr jeweils eigene Gate-A-Abnahmepunkte**.

## Ziel und verbindliche Grenze

Dies ist ein **vollständiger SQL-/Backend-Clean-Cut** mit neu entwickelter PostgreSQL-Baseline nach [CLEAN_CUT_DATABASE.md](https://github.com/Juloc/Jularr/blob/dev/docs/CLEAN_CUT_DATABASE.md), nicht die 1:1-Migration der alten Tabellen, DTOs oder SQL-Aufrufpfade. Alle **fachlichen Funktionen und Datenverantwortlichkeiten** müssen im Ziel abgedeckt oder ihr Entfall ausdrücklich entschieden werden. Exakte neue Spalten/FK/Constraints/Indizes gehören **Phase B**. Vollständige neue Service-/Logic-Implementierung, Zugriffsrechte und Client-Integration gehören **Phase D/E**.

Die neue **maßgebliche fachliche Gate-A-Liste** ist [DATABASE_CUTOVER_PHASE_A_DOMAINS.json](DATABASE_CUTOVER_PHASE_A_DOMAINS.json): **19 Fachbereiche** mit bestehenden Funktionen, Zielverantwortung, Sicherheitsrisiken und konkreter Folgephase. Diese Zuordnung ist der Abschlussnachweis, **nicht** eine behauptete 1:1-Portierung der 2.263 alten Zugriffe oder 201 historischen/Ziel-Tabelleneinträge.

## Was tatsächlich geprüft und erhalten wurde

| Nachweis | Beleg | Bewertung |
| --- | --- | --- |
| IST-Datenbank | Bereits im vorherigen Codex-Lauf verifizierter READ-ONLY-Katalog vom 10.10.2026, `docs/_phase_a_raw_schema_0.json` | **152 Anwendungstabellen**, eine Framework-Tabelle, **1.421 Anwendungsspalten**, **56/56** alte EF-Up-Migrationen; in dieser Runde nicht erneut abgefragt |
| IST-Historie | `DATABASE_CUTOVER_MIGRATIONS.csv` und `DATABASE_CUTOVER_INVENTORY_TABLES.csv` | Live-/historische Raw-SQL-/EF-Tabellen erkannt; kein altes DDL als Zielstruktur vorgeschrieben |
| Altfunktionen | `DATABASE_CUTOVER_CONSUMERS.csv`, `DATABASE_CUTOVER_SEMANTIC_ACCESS.csv`, `_phase_a_code_part_*.json` | **2.058** Source-/Test-/Bootstrap-Dateipfade, **2.263** statische DB-Zugriffe, **12** gezielt verfolgte Fachpfade; technische Referenzdaten für D/E, kein vollständiger Authorization-Test |
| Native & Web | `DATABASE_CUTOVER_ANDROID_ROUTES.csv`, Razor-/Features-Referenzen | Android hat `ApiVersion=2` und `/api/client/v1`; direkte Razor-DML, Client-/Offline-/Session-Cutover ausdrücklich in D/E |
| Parallel-PRs | `DATABASE_CUTOVER_PR_DELTA.csv` | **17** PRs des eingefrorenen Audits inklusive #945 geprüft, Integrationsvorschläge dokumentiert; nicht gleichbedeutend mit Merge-/Owner-GO |
| Neue fachliche Verantwortungen | `DATABASE_CUTOVER_PHASE_A_DOMAINS.json` | **19 Fachgruppen**, von Account-/Profilrechten bis Startup/Import, mit jedem bisher erkannten kritischen Risiko und Phase-B-/D-/E-Handoff |

**Wichtig:** Diese Zahlen beziehen sich auf den eingefrorenen, bereits überprüften Source-/PG-Stand `origin/dev e6c3c77ac8f9f41d7b6324a06c29cb772bcc68ee` und den separaten UI-Demo-Katalog. Neue Branch-Merges oder DB-Änderungen lösen eine **gezielte fachliche Delta-Prüfung** aus; sie bedeuten nicht, dass Phase A nochmals als vollständiger Legacy-Callgraph wiederholt werden muss.

## Kritische Pfade — erhalten, nicht vergessen

- **Account/Profil/Auth**: `AccountId` ≠ `ProfileId`; Rechte und Profiltransfer; Plex Login/Nonce, CSRF, Sessions/2FA/Recovery; `Pages/Account/Plex.cshtml.cs` schreibt heute direkt und benötigt einen gesicherten Command-Service/Logic-Pfad.
- **Admin/System**: `Pages/Admin/System.cshtml.cs` verändert LibraryRoots/Settings heute direkt; Modul-/Admin-/Root-Scope und zentrale Transaktion sind im neuen Service/Data/Logic zu berücksichtigen.
- **Player/Native/Offline**: PlaybackSessions, Companion-In-Memory-/Pairing-Sessions und AccountSessions sind unterschiedliche Domänen; Native-Routen und alte Offline-IDs nicht vor Client-Cutover abschalten; Plex/Jellyfin, Player, Android TV berücksichtigen.
- **Works/Medien**: Eine Work-Root für Movies/Series/Anime/Books/Manga/LightNovels/Music/Games; Album ist bereits Work-Erweiterung, Artist/Recording bleiben echte Fachidentitäten; GameRelease als `WorkVersionId`-Erweiterung. Untertitelpolicy-`ProfileId` darf **nicht** mit Benutzer-`Profiles.Id` gleichgesetzt werden.
- **Fortschritt/Reader/Bilder**: Cross-Work-Ziele, Zeit-/Lese-/Game-Position, Sync/History, ImageAssignments, Reader-Anker, Detection inkl. NoMatch und korrektes lokales WorkCard-Readmodel; nicht willkürlich alte Tabellen erhalten.
- **Operations/Provider/Acquisition**: Eine Queue, kurze Tx über SQL, Outbox/After-Commit für externe Effekte, Arr/Wanted/Import/Notifications/Learning/Localization; NAS-/Medien-/Konfigurationsdaten sind kein wegwerfbarer DB-Anhang.

## Bewusst an nachfolgende Phasen übergeben — KEIN Blocker für Gate A

| Ursprünglicher technischer Gate-A-Blocker | Verbindliche Folge |
| --- | --- |
| 61 vorläufige Legacy-Tabellendispositionen | **B:** Exakte neue Ziel-DDL aus `CLEAN_CUT_DATABASE.md`; alte Struktur/Felder müssen nicht 1:1 erhalten bleiben. |
| 37 unaufgelöste SQL-/Tracked-Tabellenreferenzen | **D/E:** Nur reale schreibende, autorisierungsrelevante, transaktionale oder funktionskritische Pfade bis auf Quelle/Target prüfen. Diagnostik-/generische Hilferoutinen nicht künstlich Fachentitäten zuordnen. |
| 2.263 Zugriffszeilen und 1.027 relevante Consumer-Pfade ohne vollständige Einzel-Kettenabnahme | **D/E:** Pro neuer Service-/Logic-/Feature-Implementierung korrekte Rolle/Caller/Resource/Module/Actor + Tests; **keine Einzel-Review aller Legacy-Referenzen in Phase A**. |
| 17 PRs ohne zusätzliche Owner-Freigabe | **B/E:** Beim tatsächlichen Merge/Port/Freeze Abstimmung einholen. Heute fachliche Änderungen berücksichtigen, keine Freigabe behaupten. |
| `AccountPermissions`/`ImageGenerationPresets` bedingt, PlexPurpose/NotificationChannels/AccountGroups/Discovery-Änderungen | **B/E:** Vor betreffender DDL bzw. Featureintegration ausdrückliche fachliche Entscheidung. Keine unbegründete Annahme, keine stillen Funktionsverluste. |

**Gate-B-Auftrag:** Nun die neue, spaltengenaue PostgreSQL-Struktur **direkt aus CLEAN_CUT_DATABASE.md und den 19 fachlichen Zielverantwortungen** entwickeln. Dabei fehlende Produktentscheidungen nur für wirklich betroffene Tabellen markieren; nach Phase B isolierte neue Greenfield-Baseline in C. Historische Migrationen werden nicht in die neue Baseline übernommen. Kein destruktiver Reset vor dem separat autorisierten Gate G.

## Nachweise/Prüfer

Die ursprüngliche Vollständigkeits-Inventur und der frühere strenge Prüfstand sind in [Issue #880 (Codex-Checkpoint)](https://github.com/Juloc/Jularr/issues/880#issuecomment-6098617476) sowie den archivierten CSV/JSONs nachvollziehbar. Der vereinfachte Gate-A-Prüfer validiert weiter den tatsächlichen READ-ONLY-Katalognachweis, Live-Tabellen-/Migrations-Abdeckung, Source-Konsistenz und die **vollständige 19-Gruppen-Fachabdeckung**. Alte Detail-Lücken werden weiterhin ausgegeben, aber als Folgearbeiten für B/D/E bewertet.

Die aktualisierten Skripte wurden in dieser GitHub-gebundenen Runde nicht in einem lokalen Checkout mit `node --test` ausgeführt. Ein automatischer grüner CI-Nachweis ist deshalb **nicht** behauptet. Vor dem Merge #945 müssen die vorhandenen `scripts/cutover/verify.test.mjs` und `verify.mjs --gate` tatsächlich ausgeführt werden. Ein erfolgreicher Gate-A-Dokumentationscheck ersetzt nie die tatsächlichen Phase-B-/D-/E-/F-Integrationstests.
