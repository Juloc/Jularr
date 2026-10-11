# Phase B — aktueller Entwurfsumfang ohne Learning

Aktueller Owner-Auftrag vom 10.10.2026: Learning wird zurückgestellt.
Der aktuelle DDL-Satz ist 01, 02, 03, 04, 06, 07, 11, 12, 14, 15, 16, 17. Nummernlücken sind
bewusst entfernte Learning-Teile, keine optionalen Baseline-Abhängigkeiten.

Statischer Stand: 141 Target-Tabellen, 29 Type-Kataloge mit 154 expliziten
Byte-Vertrags-/Seedwerten, 24 getrennte öffentliche Ressourcen-UUIDs.
Interne Ressourcen-PKs/FKs/Joins bleiben bigint. Der Verifier prüft die
vollständige Übereinstimmung von C# byte, Seed-Manifest und statischem SQL.

Die Learning-DDL, Learning-Queries, spezifischen Testgates und Profil-Learning-
Spalte sind entfernt. Die Katalogassertions verlangen ihre Abwesenheit.
Der [spätere Learning-Plan](DATABASE_CUTOVER_DEFERRED_LEARNING.md) reserviert
keine Tabellen, Codes oder Runtime-Fallbacks.

Frühe Zahlen/CI-Belege sind historische Stände. Die aktuelle Fassung wurde
frisch auf wegwerfbarem PostgreSQL 18.6 geprüft: zwölf DDL-Dateien sowie die
betroffenen Katalog-, Event-, Inbox-, Lifecycle- und Negativ-Suites. Der Inbox-
READ nutzt Profile-/Account-LastOccurredAt-Indizes vor Paging; zwei unabhängige
Sessions prüfen Recurrence, Replay und Commit-Integrität. Neue Tabellenzahl 141.
Kein aktiver DB-Eingriff und keine vollständige Anwendungssuite; CI prüft den
jeweiligen PR-Head zusätzlich.

Part 15 ergänzt Profil-Zeitzone, Ruhezeiten, Digest-Tage/-Channels, geschützte
Push-Endpunkte und Capture-Batches. Statische interne READs prüfen Scheduling
und aktuelle Route-Berechtigung. PostgreSQL prüft DST, Policy-/Profil-Grenzen,
Abschalt-Auflösung, leere Batches, getrennte Endpoints und Widerruf. Keine neuen
Enumcodes: Kalenderwerte sind 1–7, TransportKeys gehören zu registrierten Adaptern.

Part 16 schließt Auth-Zwecke und Credential-/Session-Lifecycle physisch ab.
Sieben weitere kanonische typisierte Queries prüfen Hash-/Account-/Purpose-
Grenzen, atomare Rotation, Invalidation, Recovery und TOTP. Transfer entzieht
laufendem Medien-Consent seine Mitgliedschaft; zwei parallele Sessions haben
genau einen Refresh-/Recovery-Gewinner. Keine Protokoll- oder Live-Plex-Abnahme.

Part 17 erweitert die inzwischen zentralen Provider-Flags ohne Doppelkonfiguration.
Opaque Credential-Referenzen, Grant-/Consent-Revisionen, HTTPS-Endpoint-Shape,
Owner-Section-Approval und Operation-/Generation-/Cursor-CAS wurden relational
geprüft. Abgeleitete Items benötigen eine akzeptierte Page bei Commit; Restart
und Rollback sind atomar. Admin-Grants werden vor Section-Aggregation gepaged;
Work-Kandidaten sind nach Account/Profile und aktueller Grant-Generation gefiltert.
Keine echte Remote-ACL, DNS-/SSRF- oder Client-Prüfung wird daraus abgeleitet.

Statische Prüfungen ersetzen weder PostgreSQL-Tests noch Autorisierung,
repräsentative Query-/Indexpläne oder die spätere EF-/Runtime-Abnahme.
Die [offenen Gates](DATABASE_CUTOVER_PHASE_B_DECISIONS.md) bleiben explizit.
