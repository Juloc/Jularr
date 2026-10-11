# Phase B — Cutover-Zielschema ohne Learning

Status: IN ARBEIT, nicht als Gate B freigegeben. Aktueller Owner-Auftrag vom
10.10.2026: Learning entfällt aus diesem Cutover. Frühere Einschlussentscheidungen
und Prüfstände sind durch diesen Umfang ersetzt, nicht unverändert weiterzuführen.

Keine laufende dev-/Produktivdatenbank, EF-Migration oder Runtime-Portierung wird
durch diesen Entwurf verändert. Der frische Greenfield-Cutover übernimmt weder
alte IDs noch die historische Migrationskette; ein tatsächlicher Reset benötigt
später eine eigene Freigabe.

## Aktueller Umfang

Vierzehn DDL-Dateien, in dieser Reihenfolge:

1. [01 Core](DATABASE_CUTOVER_PHASE_B_01_CORE_DRAFT.sql)
2. [02 Progress/Media](DATABASE_CUTOVER_PHASE_B_02_PROGRESS_MEDIA_DRAFT.sql)
3. [03 Media Details](DATABASE_CUTOVER_PHASE_B_03_MEDIA_DETAILS_DRAFT.sql)
4. [04 Monitoring/Notifications](DATABASE_CUTOVER_PHASE_B_04_MONITORING_NOTIFICATIONS_DRAFT.sql)
5. [06 Offline/Provider](DATABASE_CUTOVER_PHASE_B_06_OFFLINE_PROVIDER_DRAFT.sql)
6. [07 Acquisition](DATABASE_CUTOVER_PHASE_B_07_ACQUISITION_DRAFT.sql)
7. [11 Events/Delivery](DATABASE_CUTOVER_PHASE_B_11_EVENTS_DELIVERY_DRAFT.sql)
8. [12 Acquisition Coverage](DATABASE_CUTOVER_PHASE_B_12_ACQUISITION_COVERAGE_DRAFT.sql)
9. [14 Public IDs](DATABASE_CUTOVER_PHASE_B_14_PUBLIC_RESOURCE_IDS_DRAFT.sql)
10. [15 Notification Lifecycle](DATABASE_CUTOVER_PHASE_B_15_NOTIFICATION_LIFECYCLE_DRAFT.sql)
11. [16 Auth Lifecycle](DATABASE_CUTOVER_PHASE_B_16_AUTH_LIFECYCLE_DRAFT.sql)
12. [17 Provider Lifecycle](DATABASE_CUTOVER_PHASE_B_17_PROVIDER_LIFECYCLE_DRAFT.sql)
13. [18 Progress/Reader Lifecycle](DATABASE_CUTOVER_PHASE_B_18_PROGRESS_READER_LIFECYCLE_DRAFT.sql)
14. [19 Reader Content](DATABASE_CUTOVER_PHASE_B_19_READER_CONTENT_DRAFT.sql)

Die Nummernlücken sind absichtlich: die fünf Learning-Teile wurden entfernt,
nicht als leere oder optionale Baseline-Dateien behalten.

Das [Tabellenmanifest](DATABASE_CUTOVER_PHASE_B_TABLE_MANIFEST.csv) führt 151
Target-Tabellen. Die 38 bestehenden Type-Kataloge haben 187 feste Codes in
[C#-Byte-Verträgen](DATABASE_CUTOVER_PHASE_B_TYPE_CONTRACTS.cs), identischem
[Seed-Manifest](DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv) und statischem SQL.
Der Verifier lehnt undefinierte Kataloge ab. Keine Übernahme alter int-Enumcodes.

24 adressierbare Ressourcen besitzen separate öffentliche UUIDs. Interne
Ressourcen-PKs, FKs und Joins bleiben bigint/C# long. Öffentliche Identität ist
keine Berechtigung; Session-/Reset-/Refresh-/Capability-Secrets bleiben unabhängig
davon kryptografisch zufällig und gehasht bzw. geschützt.

## Learning ist ausdrücklich zurückgestellt

Keine Learning-DDL, Learning-Seeds, Learning-Reads, Profil-Learning-Optionen oder
Learning-Testgates. Der [grobe spätere Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md)
ist kein aktueller Implementierungsauftrag. Bestehende Laufzeitverbindungen
werden in D/E kohärent entfernt; keine versteckte Legacy-/Fallback-Abhängigkeit
darf die neue Baseline voraussetzen.

Reader/Player, Untertitel, Übersetzung, AI, Medienfortschritt und gewöhnliche
UI-Lokalisierung bleiben eigenständige Anforderungen des aktuellen Cutovers.
Account Groups bleibt aufgrund des ausdrücklichen Owner-Auftrags eingeschlossen.

## Nachweise und verbleibende Gates

Der frühere Commit 6926d0b6 hat alle damals vorhandenen Seeds, Typgrenzen und
Detection-/Learning-Zustandsregeln lokal und in CI geprüft. Dieser Nachweis ist
historisch und kein Nachweis für den geänderten Umfang ohne Learning.

Die aktuelle Fassung wurde frisch auf PostgreSQL 18.6 aufgebaut. Inbox-Gruppen
haben einen namespaced Schlüssel und genau einmal erfasste Event-Mitgliedschaft;
eine Commit-Constraint prüft Zähler, letzte Occurrence und Source-Event. Statische
READs autorisieren vor Paging; Read/Dismissal ändern keine Chronologie. Echte
parallele Sessions prüfen Recurrence und Replay. Profile-/Progress-Zyklen nutzen
deferred NO ACTION für explizite Deletes ohne Cascade; Orphans bleiben verboten.
Fokussierte PG-Tests und EXPLAIN sind nachgewiesen, die restlichen Gates bleiben
offen. Keine vollständige Anwendungssuite oder aktive DB-Änderung.

Der [Notification-Vertrag](DATABASE_CUTOVER_PHASE_B_NOTIFICATION_CONTRACT.md) enthält
Ruhezeiten/DST, Digest, geschützte Endpoints und aktuelle Route-Revalidierung.
Vierzehn DDL-Dateien und die betroffenen PG-Suites wurden frisch geprüft. Das beweist
Zielpersistenz, keinen existierenden externen Transport oder Runtime-Cutover.

Die [Reader-READs](DATABASE_CUTOVER_PHASE_B_READER_QUERIES_DRAFT.sql) führen
Bookmark-/Highlight-Paging und revidiertes ReaderPage-Paging in jeweils einem
statischen SELECT aus, prüfen aktuelle
Account/Profile-Mitgliedschaft vor LIMIT und geben nur öffentliche UUIDs aus.
Das zugehörige [PostgreSQL-Testset](DATABASE_CUTOVER_PHASE_B_PG_READER_QUERY_TESTS.sql)
prüft fremde Accounts, Profil-/Work-Isolation, Entzug und Disable.
Die Highlight-Sortierung hat einen Profilindex; ReaderPage-Reads binden die
öffentliche Edition-ID, optionale Kapitel-ID, VariantKey und exakte ContentRevision. Der
[Reader-Vertrag](DATABASE_CUTOVER_PHASE_B_READER_CONTRACT.md) ergänzt getypte
Layouts/Anker, immutable Publication, private Delivery und Annotation-CAS.
Playable bleibt offen.

Die [Provider-Policy-SELECTs](DATABASE_CUTOVER_PHASE_B_PROVIDER_QUERIES_DRAFT.sql)
trennen Login-, Linking- und Media-Consent-Challenges anhand ihrer festen
Purpose-Codes. Vier Provider-Freigaben sind unabhängig und standardmäßig FALSE.
Media-Connections werden nur für aktivierte Provider und gegen die aktuelle
Account/Profile-Mitgliedschaft gelesen; CredentialsStorageKey wird nicht in
Client-DTOs oder diesen SELECTs projiziert. [PG-Testset](DATABASE_CUTOVER_PHASE_B_PG_PROVIDER_POLICY_TESTS.sql).
Der [Provider-Lifecycle](DATABASE_CUTOVER_PHASE_B_PROVIDER_CONTRACT.md) ergänzt
revisionierte Grants, Owner/Sections, aktuelle Operation-Leases und gemeinsame
Page-/Checkpoint-Transaktionen. Unbestätigte Seiten können nicht committen;
Restart verwirft alte Cache-Generationen. 1000 Grants sowie 1000 Kandidaten und
999 fremde Work-Items prüfen Paging/Isolation und EXPLAIN. Reale externe
Credentials/Server-/Client-Abnahme bleiben ausdrücklich nachgelagert.

[Profilautorisierte AcquisitionRequest-READs](DATABASE_CUTOVER_PHASE_B_ACQUISITION_QUERIES_DRAFT.sql)
geben Request-/Work-/Operation-PublicIds als begrenzte, deterministische
History aus. Berechtigungen werden vor Paging geprüft; der aktuelle Request-
SQL-Test verifiziert Zugriff, Freigabe/Entzug, deaktivierte Accounts und EXPLAIN.
Acquisition-Reconciliation und weitere Arr-Reads sind weiter offen.

Der [Auth-Vertrag](DATABASE_CUTOVER_PHASE_B_AUTH_CONTRACT.md) ergänzt feste
Challenge-Zwecke, Credential-Epoch, Session-CAS, TOTP-/Recovery-Replay und
browsergebundenen Plex-Consent. Hash-Lookup und Self-Session-Paging nutzen ihre
Indizes; zwei reale Sessions bestätigen genau einen Rotation-/Recovery-Gewinner.

Der [Progress-Vertrag](DATABASE_CUTOVER_PHASE_B_PROGRESS_CONTRACT.md) ersetzt
Revision-only-CAS durch atomare Root-/Subtype-Erzeugung und einen gemeinsamen
Time-/Reader-/Game-Writer mit immutable Hash-Replay-Belegen. Reader-Anker binden
Edition und unveränderliche Contentrevision; Completed erhält explizite Herkunft
und darf nur durch manuelle UserAction entfernt werden. Continue enrichert nur
gepagte Roots; CompletedThrough ist ein SQL-Präfix mit echten Lücken/Specials.
1000 Continue-Roots und zwei unterschiedliche berechtigte Accounts prüfen Paging,
erste Root-Erzeugung, stale Writes und Rollback. Part 19 schließt Reader-
Varianten/Locator-/Publish-Verträge physisch; Replay-Retention bleibt offen.

[DECISIONS](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) nennt verbleibende Fach- und
Query-Anforderungen. Nicht alle übrigen Funktionsverträge sind abgeschlossen.
Tabellenzahlen und grüne Teilprüfungen ersetzen kein Gate B.

Nach Gate B folgt die einzelne EF-Baseline in C, die Service/Data/Logic-Portierung
in D und die vollständige Consumer-Bereinigung in E. F prüft das Gesamtergebnis;
G bleibt die separat freizugebende tatsächliche DB-Neuanlage. Kein dev-/main-Merge,
Release oder aktiver Reset ist Teil dieser Schemaänderung.
