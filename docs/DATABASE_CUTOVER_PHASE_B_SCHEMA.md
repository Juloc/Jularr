# Phase B — Cutover-Zielschema ohne Learning

Status: IN ARBEIT, nicht als Gate B freigegeben. Aktueller Owner-Auftrag vom
10.10.2026: Learning entfällt aus diesem Cutover. Frühere Einschlussentscheidungen
und Prüfstände sind durch diesen Umfang ersetzt, nicht unverändert weiterzuführen.

Keine laufende dev-/Produktivdatenbank, EF-Migration oder Runtime-Portierung wird
durch diesen Entwurf verändert. Der frische Greenfield-Cutover übernimmt weder
alte IDs noch die historische Migrationskette; ein tatsächlicher Reset benötigt
später eine eigene Freigabe.

## Aktueller Umfang

Neun DDL-Dateien, in dieser Reihenfolge:

1. [01 Core](DATABASE_CUTOVER_PHASE_B_01_CORE_DRAFT.sql)
2. [02 Progress/Media](DATABASE_CUTOVER_PHASE_B_02_PROGRESS_MEDIA_DRAFT.sql)
3. [03 Media Details](DATABASE_CUTOVER_PHASE_B_03_MEDIA_DETAILS_DRAFT.sql)
4. [04 Monitoring/Notifications](DATABASE_CUTOVER_PHASE_B_04_MONITORING_NOTIFICATIONS_DRAFT.sql)
5. [06 Offline/Provider](DATABASE_CUTOVER_PHASE_B_06_OFFLINE_PROVIDER_DRAFT.sql)
6. [07 Acquisition](DATABASE_CUTOVER_PHASE_B_07_ACQUISITION_DRAFT.sql)
7. [11 Events/Delivery](DATABASE_CUTOVER_PHASE_B_11_EVENTS_DELIVERY_DRAFT.sql)
8. [12 Acquisition Coverage](DATABASE_CUTOVER_PHASE_B_12_ACQUISITION_COVERAGE_DRAFT.sql)
9. [14 Public IDs](DATABASE_CUTOVER_PHASE_B_14_PUBLIC_RESOURCE_IDS_DRAFT.sql)

Die Nummernlücken sind absichtlich: die fünf Learning-Teile wurden entfernt,
nicht als leere oder optionale Baseline-Dateien behalten.

Das [Tabellenmanifest](DATABASE_CUTOVER_PHASE_B_TABLE_MANIFEST.csv) führt 130
Target-Tabellen. Die 28 bestehenden Type-Kataloge haben 145 feste Codes in
[C#-Byte-Verträgen](DATABASE_CUTOVER_PHASE_B_TYPE_CONTRACTS.cs), identischem
[Seed-Manifest](DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv) und statischem SQL.
Der Verifier lehnt undefinierte Kataloge ab. Keine Übernahme alter int-Enumcodes.

18 adressierbare Ressourcen besitzen separate öffentliche UUIDs. Interne
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

Die aktuelle Fassung ist auf einer zweiten frischen PostgreSQL-18.6-Datenbank
ohne Learning aufgebaut: alle neun DDL-Dateien, Katalog-, Seed-, Type-Grenz-,
Negativ- und Public-ID-Suites bestanden. Tatsächlich 130 Tabellen; die
Katalogassertions prüfen ausdrücklich die Abwesenheit von Learning. Die
28 C#-Byte-Verträge kompilieren ohne Warnungen/Fehler. CI am neuen PR-Head
bleibt der zusätzliche Nachweis; keine vollständige Anwendungssuite ausgeführt.

[DECISIONS](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) nennt verbleibende Fach- und
Query-Anforderungen. Nicht alle übrigen Funktionsverträge sind abgeschlossen.
Tabellenzahlen und grüne Teilprüfungen ersetzen kein Gate B.

Nach Gate B folgt die einzelne EF-Baseline in C, die Service/Data/Logic-Portierung
in D und die vollständige Consumer-Bereinigung in E. F prüft das Gesamtergebnis;
G bleibt die separat freizugebende tatsächliche DB-Neuanlage. Kein dev-/main-Merge,
Release oder aktiver Reset ist Teil dieser Schemaänderung.
