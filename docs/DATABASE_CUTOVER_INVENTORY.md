# Datenbank-Clean-Cut — Phase-A-Inventar

Der verbindliche Auftrag steht in [#880](https://github.com/Juloc/Jularr/issues/880#issuecomment-6098004941). **Aktueller Abnahmezustand, geprüfte Zahlen und konkrete offene Kriterien stehen ausschließlich in [DATABASE_CUTOVER_PHASE_A_STATUS.md](DATABASE_CUTOVER_PHASE_A_STATUS.md).** Das Inventar wird im bestehenden [PR #945](https://github.com/Juloc/Jularr/pull/945) fortgeführt.

Das Ziel wird vollständig neu aufgebaut: eine frische Baseline, kein Backfill, keine alte Migrationskette. Tabellenaktionen beschreiben die fachliche Zielverantwortung. `KEEP` ist kein Versprechen, alte Tabellen, Spalten oder Daten zu übernehmen. Aktuelle und historische Migrationsdateien bleiben während Phase A unangetastet.

| Kanonisches Artefakt | Inhalt |
| --- | --- |
| `DATABASE_CUTOVER_INVENTORY_TABLES.csv` | Live-/historische/zusätzliche Zielnamen, aktuelle Spalten/PG-Typen/FKs/Indizes, Zielaktion und Begründung, Fachowner, Reader-/Writer-/Caller-Kandidaten, getrennte Review-Status |
| `DATABASE_CUTOVER_MIGRATIONS.csv` | Historische EF-Up-/Raw-SQL-DDL-Nachweise und tatsächliche angewandte DB-Historie; nicht die künftige Baseline |
| `DATABASE_CUTOVER_CONSUMERS.csv` | Alle lokalen Source-/Client-/Test-/Bootstrap-Pfade mit SourceKind, Bodyindex, direkten und indirekten Referenzen; indexiert ist nicht fachlich abgenommen |
| `DATABASE_CUTOVER_SEMANTIC_ACCESS.csv` | Semantische DbSet-/Set-, gebundene Entity-Member- und SQL-/Save-/Tx-Stellen mit Datei/Symbol/Zeile, Operation, Tabellen, Caller-/Scope-/Owner-Kandidaten und selektiver Review-Evidenz |
| `DATABASE_CUTOVER_ACCESS_REFERENCES.csv` | Aggregation der direkten Zugriffsstellen pro Quelldatei |
| `DATABASE_CUTOVER_RAZOR_DB_REFERENCES.csv`, `DATABASE_CUTOVER_FEATURE_DB_REFERENCES.csv` | Direkte Zugriffe plus lokale AppDbContext-/DI-/View-Bezüge, damit indirekte Consumer erhalten bleiben |
| `DATABASE_CUTOVER_ANDROID_ROUTES.csv` | Native Routenkonstruktoren/Metadaten, Verben, Backend-Registrierung, aktuelle Scope-Grenze, Ziel-Service-/Logic-Familie, Session-/Offline-/DTO-Cutover-Abhängigkeit |
| `DATABASE_CUTOVER_PR_DELTA.csv` | Jeder offene PR, genauer Head, vollständige ChangedFiles, Schema/API-/Consumer-Delta, begründeter Integrationsvorschlag und tatsächlicher Zustimmungsstatus |

`_phase_a_raw_schema_0.json` enthält ausschließlich die read-only Live-Katalog-/History-Evidenz. `_phase_a_code_part_0.json` enthält den lokalen Source-Aufrufgraph; Teile 1–7 partitionieren den Consumer-Index. `_phase_a_search_sql_patterns_1.json` ist die maschinelle Zusammenfassung, Teil 2 die Zugriffs-Evidenz, Teil 3 PR-/Worktree-/Startup-Deltas und konkret verfolgte Fachpfade. Bestehende `_cutover_inventory_phase_a_migrations_part_*.json` und `_phase_a_raw_sql_*.json` bleiben historische Up-/SQL-Quellnachweise. Sie sind keine alternative Behauptung zum Live-Zustand.

Die Werkzeuge unter `scripts/cutover/` erzeugen und prüfen diese bestehenden Artefakte. `verify.mjs` prüft Konsistenz; `verify.mjs --gate` verweigert die Abnahme bei offenen fachlichen Zuordnungen. Neue Source-/PR-/Schema-Stände benötigen Reconciliation und erneute Prüfung. Exakte Ziel-DDL gehört zu Phase B; Runtime-/Consumer-Umstellung zu D/E; der bestehende DB-Reset ausschließlich zum gesondert freizugebenden Gate G.