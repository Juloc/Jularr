# Jularr PostgreSQL Clean-Cut: ausführbarer Masterplan

**Status: freigegebene Architektur, IMPLEMENTIERUNGSPLAN – NICHT ausgeführt (10.10.2026).**
**Master-Issue:** [#880](https://github.com/Juloc/Jularr/issues/880) (dieser Issue ist der Ausführungs-Tracker; dieses Dokument enthält die vollständigen Arbeitsanweisungen). **Backend-Fundament:** [#942](https://github.com/Juloc/Jularr/issues/942). **Architektur:** [SERVICE_DATA_LOGIC_ARCHITECTURE.md](SERVICE_DATA_LOGIC_ARCHITECTURE.md). **Verbindliches fachliches Zielschema:** [CLEAN_CUT_DATABASE.md](CLEAN_CUT_DATABASE.md), **SQL-Regeln:** [DATABASE_CONVENTIONS.md](DATABASE_CONVENTIONS.md), **Frontend:** #930, **WorkCards:** #925, **Fehler:** #853.

> **Agent-Anweisung:** Dies ist ein sequenzieller, eigenständig abarbeitbarer Plan. Führe die Phasen in Reihenfolge aus. Arbeite an einem klar koordinierten Scope gemäß AGENTS.md und .agent/project.yaml. Nutze echte Repository-Dateien/Tests als Beleg; Häkchen nur nach tatsächlich bestandenem Gate. Erfinde weder vorhandene Features noch Zielspalten, Routes oder Testresultate. **Stoppe die betroffene Phase bei fehlender Produktentscheidung oder fehlender Freigabe für einen destruktiven Schritt; nicht als stillen Kompromiss lösen.** Andere unabhängige Phasen dürfen vorbereitet werden. Schreibe nach jeder Phase einen kurzen GitHub-Status zu #880 mit Commit/PR, Testergebnis, offenen Risiken und Gate. Keine ungefragten Deployments, Releases, Merges von Fremd-Branches oder Löschung von Nutzdaten. Kein fiktiver Background-Fortschritt.

## 0. Wann genau der Cutoff stattfindet

Der Cutoff hat **zwei getrennte Zeitpunkte**:

1. **Technische neue Baseline (früh):** Sobald Inventar + ALLER spaltengenauen Target-DDL-/FK-/Index-/Enum-Entscheidungen geprüft sind, neue PostgreSQL-Baseline in **einer isolierten, wegwerfbaren lokalen Testdatenbank** erstellen und sofort mit neuer Service/Logic-Implementierung testen. Die vorhandene dev-DB und laufende Prozesse bleiben unberührt.
2. **Einziger atomarer Anwendungs-Cutover (spät):** Erst wenn **alle** direkt und indirekt betroffenen Backend-/Web-/Android-/TV-/Worker-/Provider-Consumers samt Tests auf Target stehen, die Baseline frisch installiert wurde, letzte Integrations-/Schema-Änderungen eingefroren sind und der Benutzer den vorgesehenen **destruktiven Test-Reset ausdrücklich freigibt**, in einem koordinierten Release-/Cutover-Fenster umschalten. Danach nur noch normale vorwärtsgerichtete Migrationen.

**Kein pauschales „nach Phase #942 sofort DB resetten“** und ebenso **kein Warten mit dem Schreiben/Prüfen der neuen Baseline bis alle Clients fertig sind**. Eine isolierte Target-DB ermöglicht früh echten Fortschritt ohne die alte Laufzeit zu brechen.

## 1. Reihenfolge als Gate-Kette

| Phase | Aktion | Freigabe für nächste Phase |
| --- | --- | --- |
| A | Koordination, PR-/Branch-Lage, IST-Inventar vollständig | Jeder aktuelle DB-Owner, Client und Worker erfasst |
| B | Zielschema/DDL spaltengenau abschließen, offene Invarianten entscheiden | Keine ungeklärten Struktur-/Feature-Lücken |
| C | Neue EINZIGE DB-Baseline in wegwerfbarer isolierter PostgreSQL-Instanz | Leerer Bootstrap, FK/Seed/Constraints/EF-Snapshot bestehen |
| D | Backend #942 + echte Services/Logic nach neuer Baseline entwickeln | Architektur-, Permission-/Read-only-/Rollback-Integrationstests grün |
| E | Sämtliche betroffenen Featurepfade + Web/Android/TV/Provider/Jobs umstellen | Consumer-Inventar ohne offene alte SQL/IDs/Endpoints |
| F | End-to-End-, Regression-, Last- und Desasterproben | Alle automatischen und manuellen Cutover-Gates bestanden |
| G | **Explizites Benutzer-GO** + atomarer Test-Reset/Umschaltung | Laufendes Testsystem geprüft; kein alter DB-Pfad |
| H | Aufräumen, Dokustatus, ggf. Dev→Main Release separat | Greenfield-Baseline als einzige Historie, neue Forward-Migrationen |

A/B und das nicht-destruktive #942-Fundament dürfen technisch parallel vorbereitet werden, **aber keine Schema-Annahme als endgültig implementieren, bevor B bestanden ist**. Neue Features aus parallelen Branches müssen vor B-Freeze inventarisiert und nach D/E erneut auf Änderungsbedarf geprüft werden.

## Phase A – IST-Nachweis und fachliche Abdeckung (vereinfachter Owner-Gate)

**Status:** [Phase-A-PR #945](https://github.com/Juloc/Jularr/pull/945) dokumentiert die vom Owner vereinfachte Abnahme. PostgreSQL-Katalog und EF-/Raw-SQL-Inventar wurden bereits früher schreibgeschützt aufgenommen. **Kein weiterer Datenbankzugriff und kein vollständiges manuelles Einzelreview aller alten Consumer nötig.**

- [x] Alten DB-Stand **als IST-Nachweis** sichern: Live-PG-Katalog, 56 angewandte Migrationen, EF-/Raw-SQL-Quellen und historische Ausnahmen. Altstruktur bestimmt nicht das Ziel.
- [x] Alle im bestehenden System und in relevanten PRs erkannten Funktionen **fachlich gruppieren**; Zuordnung der **19 Zielverantwortungen** mit kritischen Sonderfällen und konkreten Folgeschritten in [DATABASE_CUTOVER_PHASE_A_DOMAINS.json](https://github.com/Juloc/Jularr/blob/docs/cutoff-phase-a-inventory-20261010/docs/DATABASE_CUTOVER_PHASE_A_DOMAINS.json). Keine zweite Medien-/Work-/Operations-Wahrheit einführen.
- [x] Kritische Identitäts-/Berechtigungs-/Transaktions-/External-Effect-/Native-/Offline-Sonderfälle markieren (Accounts ≠ Profiles, Plex, Admin-DML, Player, Reader, Progress, Games, SQL Reads). Bestehenden Sourceindex als Suchgrundlage behalten; unsichere Implementierungspfade gezielt in D/E prüfen.
- [x] PR-/Worktree-Änderungen als **fachliche Delta-Risiken** einordnen (integrieren, zum Ziel portieren, Alternativen bewusst wählen oder Erweiterung separat behandeln). Merge-/Owner-Freigabe ist **nicht** Gate A, sondern Zeitpunkt des Integrations-Freeze.
- [x] Offene Produktentscheidungen, nicht abgeschlossene historische Table/Field-Mappings, generische SQL-Fundstellen und kritische neue Ziel-Datenregeln ausdrücklich **Phase B bzw. D/E** zuordnen. Nicht stillschweigend alte Features streichen.

**Gate A: BESTANDEN unter den vereinfachten, ausdrücklich vom Owner vorgegebenen Bedingungen.** Vorhandene Fachfunktionen sind Zielbereichen zugeordnet; keine 1:1-Migrationspflicht. **Nicht nötig:** jede der >1.000 Legacy-Consumer-Dateien einzeln autorisieren, 2.263 alte Zugriffsstellen bestätigen, 61 alte Tabellennamen endgültig in A entscheiden, alle Zielspalten/FKs/Indizes entwerfen oder jede PR-Owner-Mergefreigabe in A einholen. Diese Detailnachweise werden beim Implementieren und Testen der jeweiligen **neuen** Entitäten/Services erzwungen.

**Nächster Schritt:** Phase B darf starten und entwickelt die **neue** spaltengenaue DDL anhand der fachlichen Zielstruktur in `CLEAN_CUT_DATABASE.md`; offene fachliche Entscheidungen verhindern nur die betroffenen DDL-Teile. Rückfragen zu realen Produktentscheidungen bündeln, keine generelle Vorab-Sperre für unabhängige Schemaabschnitte. Der bestehende dev-Datenbankstand bleibt unverändert.

## Phase B – Physische Ziel-DDL und fachliche Invarianten finalisieren

Die Fachentscheidungen in `CLEAN_CUT_DATABASE.md` sind bindend, aber **kein fertiges exaktes PostgreSQL-DDL**. Für **jede** Zieltabelle erfassen: exakter Name, Column/PostgreSQL-Type, nullable/default, PK, FK (genaue Referenz/ON DELETE), UNIQUE/CHECK, Seed/C#-Enum-Werte, Index/Sortplan, Datenbesitzer, Migration-/Install-Owner, Schreib-/Read-Consumer. Speichere signierbares Schema-Inventar und/oder prüfbare DDL-Entwürfe; keine spekulativen Tabellen.

- [ ] **Accounts/Profiles/Auth:** Accounts (Email `citext` unique und required, DisplayName nicht unique, Rollentyp); Profiles (OwnerAccountId/UiLocaleId), AccountProfiles (Link ohne CanManage), Profile-PIN vs Login/2FA, Password/Passkey/WebAuthn/TOTP/Recovery/ExternalLogin/Challenges, AccountSessions (Hash/Revocation/selected profile), PlaybackSessions (andere Identität!), Account-/Profile-Berechtigungs- und Transfer-Invarianten. Alle Profile-bezogenen FKs bleiben am stabilen ProfileId; Transfer als atomarer Ablauf.
- [ ] **Ein Work für alle Medien:** `Works.Id bigint`; Movie/Series/Book/Manga/LightNovel/Music/**Game**; Anime = Klassifikation, Audiobook = Edition/Asset bei gemeinsamer Work-Identität; keine parallelen Games/Anime/etc.-Roots. `WorkTitles`, lokalisierte übrige Fakten, FieldProvenance, Provider ExternalIdentities mit Namespaces, Genre/Klassifikation, WorkRelations versus Franchises/FranchiseWorks.
- [ ] **Logische und physische Medien:** WorkSeasons/WorkEpisodes/WorkVolumes/WorkChapters/WorkTracks; WorkEditions/WorkVersions/MediaAssets/StoredFiles/MediaTracks/MediaTechnicalAnalyses/LibraryRoots, Offline-Status und Dateipräsenz in DB. GamePlatforms sowie GameReleases **als WorkVersionId-PK/FK-Extension**, versionierte Multi-Disc/GameReleaseStoredFiles, FileRole, Reihenfolge, Regions/Hashes/Launcher. Checksums am passenden File/Release-Owner, keine doppelte Asset-ID.
- [ ] **Progress:** MediaProgress pro Profile + exaktem Unit-Target; korrekte UNIQUE-NULL-Semantik, WorkId↔Episode/Chapter/Track/Edition-FK-Membership, Time/Reading/Game Position-1:1, exakt passender Subtyp, Completion nicht automatisch aus Seek; Activity-/MediaPlaybackHistory separat; Offline-/Client-Retry/Checkpoint-Versionen/Idempotenz und mögliche Savegame-Modelle. **Offene Blocker:** relational saubere Erzwingung Position↔Subtype + polymorpher Targets, ohne versteckte Trigger-Geschäftslogik.
- [ ] **Artwork und Reader:** Images/ImageTypes/ImageAssignments mit `num_nonnulls(target FKs)=1`, zulässige Typ/Target-Kombinationen; Work-/Chapter-/MediaChapter-Images, Locale/Region/Source/Priority/Manual-Fallback; ImageGenerationRequests/Operation-Verbindung. ReaderContent/Pages/Bookmarks/Highlights/Textanker/Edtition-Fassung/Übersetzung/AI/Learning ohne parallele Identitäten.
- [ ] **Player/Segments:** MediaChapters und MediaSegments pro exaktem MediaAsset; Intro/OP, Recap, Outro/ED, Credits, Preview dürfen überlappen; eindeutige StartMs/EndMs-Checks, manuelle Override-/Quelle-/Algorithmuspriorität. MediaDetectionRuns inkl. erfolgreichem **No-Match** und algorithm-/fingerprint-basiertem Rerun; Medienanalyse, Sprites/Trickplay nur bei echter persistenter Notwendigkeit als Tabelle, sonst Cache. Nicht vorhandene Recap/Credits-/Video-Erkennung nicht als fertig behaupten.
- [ ] **Wünsche/Import/Arr/Provider/Operations:** Requests/Rules/Wanted/Downloads/Release-Selection/Acquisition/Import/LibraryScan/Metadata/StorageReconciliation, externe Plex/Jellyfin-Zuweisungen und Medienrechte, langlebige Operations/Logs/Retry/Claim/Queue, Downloads und Benachrichtigungen; eine vorhandene Operations-Quelle ohne Parallel-Queue. External Migrations Center bleibt separates **unterstütztes Import-Feature**, historische interne AniLingo/SQLite-Bridges entfallen.
- [ ] **Watchlist/Collections/Locale:** `WatchlistEntries(ProfileId,WorkId)`, Collections/CollectionWorks/ProfileRatings/MediaProgress und Read-Projektionen; UI-Lokalisierung allein aus UiLocales/UiTranslationMessages/UiTranslations, einschließlich exact→parent→English, Bild-/Titel-Region-Fallback und Localization für Errors. Kein doppeltes ErrorTranslations/Media-State-Fact.
- [ ] **Type-/Naming:** plural quoted PascalCase Tables, bigint Id/long, Composite PK für Junctions, genaue `FK_<Table>_<Target>` / `CK_` / `UX_` / `IX_` Namen; fixe Persisted `enum : byte` mit expliziten Werten und FK auf stable-seeded `...Types` `smallint`, `Key` übersetzt über UI-Catalog. Kein unspezifisches StatusId/TargetType+Id-EAV, keine heimlichen ON DELETE CASCADE-Workflows/Trigger-Logik.
- [ ] **Indexes/SQL-Budgets:** gezielte Hotpaths aus `CLEAN_CUT_DATABASE.md §8.3` (Login, Picker/Transfer, Work/search, WorkExternalIdentity, episode/chapter, StoredFiles root/path, Watchlist sorted, Continue, ImageAssign, Languages, Segments, DetectionRuns, Operations claim, history, translations); keine pauschalen FK-Indizes, keine Blind-Includes, ggf. `citext`/`pg_trgm` Extension-Installation.
- [ ] **Entscheidungsregister:** unklare tatsächliche Anforderungen als eigene konkrete Frage mit betroffenen Tabellen/Tests in #880 oder verlinkter ADR markieren und vor DDL abschließen. Vorhandene Produkteingaben respektieren, keine heimlichen Alternativen.

**Gate B:** Die vollständige IST→SOLL-Matrix und das konkrete spaltengenaue DDL sind geprüft, keine ungelöste Invariante/Feature-Verlust, alle Enum-Seeds/Indexes/Extensions festgelegt. **Schema-Freeze** nur im koordinierten Scope. Alte Feature-PRs mit DB-Änderungen erneut abgleichen.

## Phase C – EIN sauberer PostgreSQL-Baseline-Stand, ausschließlich isoliert

- [ ] Eine **separate wegwerfbare** PostgreSQL-Testdatenbank / Compose-Projekt mit eigener DB/Volume und klar dokumentierter Isolierung aufsetzen. **Nicht** die laufende dev-DB, Images/NAS, Nutzer-Volumes, bestehende Jobs oder Credentials verändern. Backup-/Cleanup-Strategie für isolierte Instanz dokumentieren.
- [ ] Auf separatem Integrationsbranch alle Ziel-Entities/EF-Konfigurationen, sinnvolle FK/Indexes/Constraints, PG-Extensions, Enum-Seeds und **genau EINE** Greenfield-Baseline-Migration samt `AppDbContextModelSnapshot` erzeugen. Die alte Migrationskette nur im ausdrücklich dafür vorbereiteten Target-Branch ersetzen, **niemals vorzeitig im laufenden dev**.
- [ ] EF-Model/Raw-SQL-Dialekt/DesignTimeFactory, PostgreSQL provider version, connection registration, Migration Bridge/WorkIdFollowUp, Migration Epoch und Install-/Startup-Verhalten mit neuem Stand synchronisieren. Der neue Start soll bei leerer DB exakt eine Baseline ausführen; keine alte Einmalmigration, Kompatibilitätsbrücke, Dual-Write oder separate Games/Story-Wurzel. **Nach dem Cutover** folgen reguläre neue forward migrations, die Baseline wird nicht ständig neu gesquasht.
- [ ] Test: DB von **0** aufbauen, schema inspect pg_catalog, Extensions/Seeds kontrollieren, zweite Initialisierung idempotent; Migration/ModelSnapshot keine Pending-Changes; PK/FK/UNIQUE/CHECK/NULL/FK-targets und deliberate Rejection Cases, Cross-WorkUnit-Bindings und PositionSubtype, positive Locale/Auth/Foreign Identity/Media assignments.
- [ ] Eine zweite frische leere DB erneut vollständig bootstrappen: reproduzierbar, keine Daten aus der alten DB erforderlich. Neuinstallations- und Test-Fixtures/Owner-Bootstrap dokumentieren.

**Gate C:** Isolierte Neuinstallation wiederholbar; Constraint-/Seed-Tests gegen PostgreSQL grün; eine einzige neue Baseline. Laufender dev-Datenbankstand unverändert.

## Phase D – Backend Runtime/Logic + vertikale Referenz auf ZIELSCHEMA

**Abhängigkeit:** #942 kann seine neutrale Grundarchitektur vorher entwickeln, aber Real-SQL-Services müssen gegen die **freigegebene Target-DDL aus B/C** getestet werden.

- [ ] `Data.<Area>.<Entity>.V1` und `Service.<Area>.<Entity>.V1` nach Entity-Datei getrennt; internes `Data.<Entity>` und `Logic.<Entity>` unversioniert/rollenfrei. `Service`: Gate/SELECT/mehrere Logic-Schritte, kein DML/Provider-/Filesystem-Side-Effect. `Logic`: echte Änderungen/Invarianten/Locks und nicht-DB-Aktionen. Kein `Logic -> Service` oder `Web -> Logic`.
- [ ] Neutrales SqlContext mit **derselben** Connection/Transaction über alle Logic-Funktionen; GetOperationType(Read/Create/Update/Delete/Execute) startup-validiert. Read: nur ReadSql + tatsächliche PostgreSQL READ ONLY Tx, Schreiboperationen: automatisch kurze atomare READ WRITE Tx; Execute: bewusst explizite Policy, kein mehrstündiger DB-Scope.
- [ ] Gesamtes DTO direkt an SqlExecutor, **nur** echte statische SQL-Platzhalter bindbar, Scope Actor/Profile serverseitig reserviert; Null/ByteEnum→smallint/Arrays/jsonb/citext/Kommentare/Quotes/Casts/kollisionen testen. Page/PageSize 1/25/max100, stable SortKeys + genau ein Default, statische Sortquery, SQL-pagination vor bounded JSON LATERAL-Children.
- [ ] Zuerst Account/Profile Reads/Permissions/Locale, dann User vs Admin Account Update (Sessions/Audit/Transfers und **mehrere** Logic-Calls in **einer** Tx), dann WorkCards/Watchlist/Library/Continue als ein SQL-Read je Page. Zentraler Error-/Incident-Vertrag #853; optionaler ResultType kann strengere Permission haben; Auth/Public/System Caller-Nicht-Fälschbarkeit.
- [ ] PostgreSQL-Integration: READ ONLY blockiert Write/FOR UPDATE, Pool-Session wird korrekt resettiert; Error/Cancel/2nd-/3rd-Logic-Fehler => vollständiger Rollback; DomainInvariant, Actor/Profile Scope, Race/Locks, NoData/PATCH-Ambiguität, 0-row Update ≠ blind NotFound, keine N+1, nachvollziehbare EXPLAIN (ANALYZE, BUFFERS)-Pläne.

**Gate D:** Kompilierende und getestete Basis plus **mindestens je ein echtes** Read, paginierter Multi-Join-Read, User/Admin-Multi-Logic-Update und System-Operation-Scope auf Ziel-DDL; keine Scheindaten-/Schein-ReadOnly-Garantie.

## Phase E – Vollständige Feature- und Clientumstellung (Migrationsmatrix abarbeiten)

**Für JEDE Zeile des Phase-A-Inventars:** alter Entrypoint/SQL/Tabellen-/FK-/Enum-/ImageId-/WorkId-/ProfileId-/URL → neuer Entity-Owner / Service/Logic / DTO/API / SQLite- bzw. Legacy-Abschaltung / betroffener Client / Test / Commit. Eine Funktion gilt erst als migriert, wenn **alle Consumer** für sie aktualisiert sind.

- [ ] Account Setup/Login/External/Plex login/Passkeys/TOTP/Recovery/Auth-Challenges/Session-Revocation/Profile Auswahl und Transfer, Profile-lokalisierte Fehler, Permissions, Admin Users/Policies/Groups.
- [ ] Work/Media-Canonicalization inkl. **Game**, Movie/TV/Anime-Klassifikation, Book/Audiobook/LightNovel/Manga/Music, Franchises, WorkTitles/Provider-Ids, Editions/Versions/Assets/StoredFiles, Disc/Hashes, lokale Verfügbarkeit und ImageId-/ImageAssignment-URLs.
- [ ] Library/Search/Discovery/Collections/Watchlist/Continue/Recommendations/Calendar, Reader/Themes/Reading Units/Bookmarks/Highlights/Translations/Learning/AI, Kapitel-/Segmente-/Detection-Daten, Tracking/Progress/Offline Sync/History, Download/Monitoring/Wanted/Requests/Rules/Import/Arr/Scanner und Provider-Synchronisation.
- [ ] Player direct/remux/transcode, HLS, WAN, renditions, skip Intro/Recap/Outro/Credits, PlaybackSessions, Audio-/Subtitle-Tracks, TV UI/Android native inklusive Account- vs Profile-Auswahl; Plex/Jellyfin scoped grants/reconciliation nach ihren aktuellen Branch-Ständen. **Keine neue NAS-Abhängigkeit bei UI/Watchlist GET**.
- [ ] System/Background Jobs mit EINEM Operations-Modell, Queue vs Direct vs Force, cancellation/retry/restart, Notification/Email/outbox, Scan/Provider/Caches, Module Disable; externe APIs/Provider-Sync nicht als SQL-Transaktion ausgeben.
- [ ] Web Razor/API/JS und mobile clients verwenden dieselben autorisierten Serviceoperationen, User/Admin/System getrennt; Route-Migration `/api/v1/...` / `/api/v1/admin/...`. Entferne ungenutzte Legacy-Routes **zusammen mit** aktiv umgestellten Clients, nicht früher.
- [ ] Alte Tabellen/Entitäten/StoredQueries/MigrationBridges/Startup-Workarounds/Legacy-Tests/EF-Model-Konfigurationen/DI-Registrierungen nach tatsächlicher Abdeckung entfernen. **Kein** dauerhafter Parallelpfad/Fallback/doppelte Schreibquelle.
- [ ] Neu auf target IDs laufende Clients/Offline-Caches müssen nach Reset frische Login/Profile/Work-IDs laden. Session-, Token- und Offline-Queue-Generation/Reset-Policy ausdrücklich festlegen; alte Client-Daten dürfen keine falschen neuen bigint IDs adressieren. Konfiguration, NAS/Medienfiles und externe Systeme **nicht** als „wegwerfbare DB“ mitlöschen. Cache/Image-/File-Reconciliation für neuen DB-Stand separat kontrollieren.

**Gate E:** Die Inventarmatrix hat **keinen** ungeklärten Reader, Writer, Worker, Endpoint oder Client mehr. Alle parallelen Branch-Änderungen wurden integriert/portiert/deferiert dokumentiert. Alle nötigen Integrationstests grün.

## Phase F – Freigabetests / Dry-Run der gesamten Anwendung

- [ ] Wiederherstellbarer, dokumentierter **Dry-Run** auf komplett frischer isolierter PG-/Compose-Instanz mit neuem Code, einem Baseline-Migrationspfad und den nötigen Seed-/Bootstrap-Daten. Keine alten Datenbackfills. Bei erneuter Installation gleiche DDL/Seeds/Setup.
- [ ] Required solution validation aus `.agent/project.yaml`: `dotnet restore Jularr.sln`, `dotnet build Jularr.sln --no-restore`, `dotnet test Jularr.sln --no-build`; zusätzlich relevante npm/frontend/Android/TV-Gradle-/Browser-/UI-/Docker-Compose-Checks tatsächlich ausführen, nicht durch Dotnet-Tests ersetzen. Relevante CI-Checks/PRs bestätigt.
- [ ] Sicherheit: Account vs Profile, Zugriff fremdes Profile/Work/LibraryRoot, Owner/Admin/User/System, Module deaktiviert/laufender Worker, SQL Injection/typed bind, READ ONLY, CSRF, Rate-Limits/Session reset, Login/2FA/Passkey, kein SQL-/Geheimnis-Leak.
- [ ] Feature-End-to-End: initial Owner+Account/Profile, Profile-Transfer, Suche/Import/Arr/Provider, Library offline/online, Watchlist/Collection/Continue, Reader Book/Manga/Novel, Movies/TV/Music/Audiobook/Games, MediaProgress Time/Reading/Game, Offline Retry, Images/Chapter, Intro/Recap/Outro/Credits/Detection NoMatch, WorkCard Locale/Bilder/Audio/Sub/Libraries, Player Plex/Jellyfin/Android TV, Job/Notifications/Restart.
- [ ] Performance: SQL plan index/fk hotpaths, paged WorkCard mit mehreren 1:N, stabile tie-breaker, kein N+1, JSON size, Read-only-Tx overhead, ConnectionPool/CPU/RAM auf ressourcenschwachem Host; Null/empty/page overflow, missing arrays, locale fallback.
- [ ] Fehlerproben: frischer DB-Bootstrap fehlschlägt, Phase G nach Serverstart fehlschlägt, inkompatibler Android/TV Client, Worker-Claim nach Crash, Nach-Commit-Outbox verloren/Retry, SQL-Snapshot/Locks/Konflikte. Festlegung **Rollback**: alte App + kompatibler alter DB-Snapshot nur mit getesteter Wiederherstellung, nicht neues Schema per Reverse-Migration erraten; bei absichtlich entsorgter Test-DB kann stattdessen vollständige Neuinstallation gewählt werden. NAS-/Filesystem-Zustand separat schützen und dokumentieren.
- [ ] Build/CI, Database schema review, Security review, API-/UI-/native Consumer-Abnahme mit Commit-SHAs und PostgreSQL-/Testdaten-Versionen als Evidence auf #880 verlinken.

**Gate F:** Sämtliche Tests nachweislich erfolgreich; Schema-/Client-Integrations-Freeze; Rollback-/Neuinstallationspfad real getestet. Nicht abgenommene produkt-/featurekritische Funktion => **kein Cutover**.

## Phase G – Ein einziges autorisiertes Cutover-Fenster (DESTRUKTIV)

**STOP-SCHRANKE:** Diesen Abschnitt **erst nach expliziter Benutzerfreigabe** für konkrete Testinstanz, Datenbank/Volume und Umfang ausführen. Keine Freigabe allein aus diesem Dokument, vorherigen Go-Aussagen, PR-Merge oder test-only-Annahme ableiten.

- [ ] Bestätige schriftlich: dies ist die beabsichtigte zurücksetzbare TEST-Instanz; User-Daten/DB können entfernt werden, NAS/Medien-Dateien/Konfiguration/Provider-Credentials dürfen dabei **nicht** gelöscht werden; Auflistung betroffener Volumes/Backups und Recoveryentscheidung.
- [ ] Freeze aller mutierenden Jularr-Prozesse/Jobs/UI/API/Scanner/Arr/Provider-Sync/Clients, wartende Operations behandeln. Notiere geprüften Ausgangs-Commit/PR-Merge-Stand, Schema-Epoch, optional DB-/Config-Snapshot; kein heimliches Fortlaufen von Alt-Workern.
- [ ] **Nur für erlaubte DB**: alte Testdatenbank/Volume gezielt neu initialisieren bzw. neue frische DB als aktive Verbindung einrichten. Es gibt **keinen Legacy-Backfill und keine Dual-Writes**. Ein Build enthält Schema Baseline + EF Snapshot + neue Logic/Services + alle Clients/Routen; keine Mischinstallation.
- [ ] Automatischen Startup/Seed/Owner-Setup (inkl. Secrets/Session/Operation-Generation) auf neuem DB-Stand kontrollieren, Inkompatibilitäten/alte offline IDs erkennen und neutralisieren; alle Read/Write-Pfade lokal smoke-testen. Hintergrundoperationen erst nach erfolgreichem Setup kontrolliert freigeben.
- [ ] Bei Fehler: stoppe alte/neue Worker, rollback gemäß **getestetem** vollständigem Code+DB/Neuinstallations-Pfad, nicht eine einzelne EF-Migration rückwärts ausführen. Keine fehlerhafte halbe Umschaltung live lassen.
- [ ] Erst nach erfolgreichem Smoke die Testinstallation öffnen, Login/Web/AndroidTV/Player/Reader/Arr/Plex/Jellyfin/Locale/Images/Jobs aus realer UI prüfen. Post-Cutover Erkenntnisse und mögliche Datenverluste transparent protokollieren.

**Gate G:** Testinstallation läuft ausschließlich gegen die neue Baseline, alle Kernfunktionen verfügbar, Logs gesund, keine alte DB-Verbindung/alten Seed-/WorkId-Maps.

## Phase H – Aufräumen und Übergabe

- [ ] Kein alter EF-Snapshot/Migrationspfad/Legacy-Reader/-Writer/Parallelroot mehr im finalen Source, kein unnötiger SQLite/AniLingo/Medienroot-Bridge; **External Migration Center** für externe Quellen ist weiterhin ein zulässiges eigenes Feature.
- [ ] Neue normale Forward-Migrations-Policy, Upgrade-/Install-Anleitung, Datenbank-/Backup-/Recovery-/Epoch-Policy, C#-/API-/UI-Dokumentation und Build-Skripte anpassen. Kein weiteres Baseline-Neuschreiben nach Deployment ohne erneute explizite Testreset-Freigabe.
- [ ] Übergebe #880: finaler DDL/Schema-Index, vollständiger Disposition-/Consumer-Inventarstatus, PR-/Commitliste, getestete Migration/Installation, PostgreSQL-Tests/EXPLAIN, benötigte manuelle Schritte, offene bewusst verschobene **Feature-Erweiterungen**. Kritische Abdeckungslücken dürfen nicht als „deferred“ versteckt werden.
- [ ] `dev → main` / Docker Release und Compose-Anpassungen sind **separate Release-Freigaben**; nicht automatisch aus einem erfolgreich dokumentierten Cutover auslösen.

**Erledigt bedeutet:** eine kanonische DB mit clean Baseline, keine duplizierten Works/Games/Progress/Auth/Images/Job-Pipelines, alle bestehenden zugesagten Funktionen und User/Admin/System-Sicherheitsgrenzen funktionieren, alle relevanten Consumers sind angepasst, Datenbankzugriffe erfüllen den Service/Logic-Clean-Cut. **Das Plan-Dokument selbst gilt nicht als erledigte Implementierung.**
