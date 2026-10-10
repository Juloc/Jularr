# Phase B — aktueller Entwurfsumfang ohne Learning

Aktueller Owner-Auftrag vom 10.10.2026: Learning wird zurückgestellt.
Der aktuelle DDL-Satz ist 01, 02, 03, 04, 06, 07, 11, 12, 14, 15. Nummernlücken sind
bewusst entfernte Learning-Teile, keine optionalen Baseline-Abhängigkeiten.

Statischer Stand: 137 Target-Tabellen, 28 Type-Kataloge mit 145 expliziten
Byte-Vertrags-/Seedwerten, 19 getrennte öffentliche Ressourcen-UUIDs.
Interne Ressourcen-PKs/FKs/Joins bleiben bigint. Der Verifier prüft die
vollständige Übereinstimmung von C# byte, Seed-Manifest und statischem SQL.

Die Learning-DDL, Learning-Queries, spezifischen Testgates und Profil-Learning-
Spalte sind entfernt. Die Katalogassertions verlangen ihre Abwesenheit.
Der [spätere Learning-Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md) reserviert
keine Tabellen, Codes oder Runtime-Fallbacks.

Frühe Zahlen/CI-Belege sind historische Stände. Die aktuelle Fassung wurde
frisch auf wegwerfbarem PostgreSQL 18.6 geprüft: zehn DDL-Dateien sowie die
betroffenen Katalog-, Event-, Inbox-, Lifecycle- und Negativ-Suites. Der Inbox-
READ nutzt Profile-/Account-LastOccurredAt-Indizes vor Paging; zwei unabhängige
Sessions prüfen Recurrence, Replay und Commit-Integrität. Neue Tabellenzahl 137.
Kein aktiver DB-Eingriff und keine vollständige Anwendungssuite; CI prüft den
jeweiligen PR-Head zusätzlich.

Part 15 ergänzt Profil-Zeitzone, Ruhezeiten, Digest-Tage/-Channels, geschützte
Push-Endpunkte und Capture-Batches. Statische interne READs prüfen Scheduling
und aktuelle Route-Berechtigung. PostgreSQL prüft DST, Policy-/Profil-Grenzen,
Abschalt-Auflösung, leere Batches, getrennte Endpoints und Widerruf. Keine neuen
Enumcodes: Kalenderwerte sind 1–7, TransportKeys gehören zu registrierten Adaptern.

Statische Prüfungen ersetzen weder PostgreSQL-Tests noch Autorisierung,
repräsentative Query-/Indexpläne oder die spätere EF-/Runtime-Abnahme.
Die [offenen Gates](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) bleiben explizit.
