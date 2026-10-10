# Phase B — aktueller Entwurfsumfang ohne Learning

Aktueller Owner-Auftrag vom 10.10.2026: Learning wird zurückgestellt.
Der aktuelle DDL-Satz ist 01, 02, 03, 04, 06, 07, 11, 12, 14. Nummernlücken sind
bewusst entfernte Learning-Teile, keine optionalen Baseline-Abhängigkeiten.

Statischer Stand: 130 Target-Tabellen, 28 Type-Kataloge mit 145 expliziten
Byte-Vertrags-/Seedwerten, 18 getrennte öffentliche Ressourcen-UUIDs.
Interne Ressourcen-PKs/FKs/Joins bleiben bigint. Der Verifier prüft die
vollständige Übereinstimmung von C# byte, Seed-Manifest und statischem SQL.

Die Learning-DDL, Learning-Queries, spezifischen Testgates und Profil-Learning-
Spalte sind entfernt. Die Katalogassertions verlangen ihre Abwesenheit.
Der [spätere Learning-Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md) reserviert
keine Tabellen, Codes oder Runtime-Fallbacks.

Frühere Zahlen/CI-Belege mit Learning sind historische Stände und keine
Abnahme des geänderten Schemas. Die aktuelle Fassung wurde auf einer zweiten,
frischen wegwerfbaren PostgreSQL-18.6-Datenbank geprüft: neun DDL-Dateien und
fünf betroffene SQL-Suites erfolgreich, tatsächliche Tabellenzahl 130. C#-Byte-
Verträge kompilieren ohne Warnungen/Fehler. Kein aktiver DB-Eingriff und keine
vollständige Anwendungssuite; zusätzlicher CI-Nachweis am geänderten PR-Head.

Statische Prüfungen ersetzen weder PostgreSQL-Tests noch Autorisierung,
repräsentative Query-/Indexpläne oder die spätere EF-/Runtime-Abnahme.
Die [offenen Gates](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) bleiben explizit.
