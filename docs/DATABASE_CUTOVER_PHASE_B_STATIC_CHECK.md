# Phase B – statischer DDL-Check (acht SQL-Entwürfe)

**Status:** Die **statischen** FK-/Objekt-/Namensprüfungen liefern keinen Fehler. **Kein PostgreSQL-Lauf, kein EF-Snapshot und KEINE Gate-B-Freigabe.** Die Ergebnisse beziehen sich nur auf SQL-Text aus dem Branch `docs/cutoff-phase-b-schema-20261010`.

| Prüfgegenstand | Ergebnis |
| --- | ---: |
| Zieltabellen (`CREATE TABLE`) | **152** |
| Erfasste einfache Spaltendeklarationen | **718** |
| FK-Deklarationen inkl. `ALTER TABLE ... ADD CONSTRAINT` | **222** |
| Unbekannte FK-Zieltabellen | **0** |
| FK-Zielspalten ohne passend deklariertes `PRIMARY KEY`/`UNIQUE` | **0** |
| FK-Spalten mit unterschiedlichem einfachen PG-Deklarationstyp | **0** |
| Doppelte Tabellennamen | **0** |
| Doppelte Spaltennamen innerhalb der Tabelle | **0** |
| Doppelte Constraint- oder Indexnamen | **0** |

**Dateiumfang:** 01 Core 45 Tabellen; 02 Progress/Media 36; 03 Media/Music 10; 04 Monitoring/Notifications 9; 05 Learning Curriculum 18; 06 Offline/Provider 5; 07 Acquisition Profiles/Indexers 17; 08 Learning Unit/Card/FSRS 12. Alle acht Drafts sind im [Tabellenmanifest](DATABASE_CUTOVER_PHASE_B_TABLE_MANIFEST.csv) erfasst.

**Genauigkeitsgrenze:** Das Verfahren liest `CREATE TABLE`, benannte Constraints, `REFERENCES`, einfache Typ-Deklarationen und benannte Indizes. Es ist **kein PostgreSQL-Parser**, keine Prüfung semantisch gleichwertiger Typauflösung oder impliziter Casts jenseits der einfachen deklarativen Typ-String-Gleichheit, Index-Selektivität, `GENERATED ALWAYS`-Auswertbarkeit, zirkulärer `DEFERRABLE`-FK-Semantik, Seed-IDs oder Autorisierungsmodelle. Auch CHECK-Expressions, mehrphasige `ALTER`-Verknüpfungen und dynamische SQL-Syntax sind nicht vollständig geprüft.

**Vor Gate B zwingend:** Die finale Gesamt-DDL auf isolierter frischer PostgreSQL-Zielversion anwenden und Constraints durch negative/positive Transaktionstests bestätigen; echte `enum : byte`-Seeds und DTO-Typen abgleichen; WorkCard-/Continue-/Acquisition-/Learning-SELECTs mit Testdaten ausführen und `EXPLAIN (ANALYZE, BUFFERS)` auswerten; vor finaler Baseline offene Product-Entscheidungen und persistente Funktionslücken schließen. Das ist **nicht** die aktive dev-Datenbank. Ein destruktiver Cutover gehört nach separater Genehmigung zu Phase G.
