# Phase B – statischer SQL-Entwurfscheck (kein PostgreSQL-Test)

**Prüfung:** GitHub-Dateitexte der drei Entwurfs-SQL-Dateien, keine SQL-Ausführung. Eine heuristische Parserprüfung ist **kein** Beweis für PostgreSQL-Syntax, DEFERRABLE-Korrektheit, EF-Abbildung, Seed-Konsistenz oder Anwendungsfunktion.

| Prüfung | Ergebnis |
| --- | --- |
| Neue SQL-Tabellenentwürfe | **91** |
| Erfasste einfache Spalten-Deklarationen | **414** |
| FK-Referenzen auf bekannte Tabellen | **127** |
| FK referenzierte Zielspalten vorhanden | **kein statischer Fehler** |
| FK-Zielspalten entsprechen PK/UNIQUE-Deklaration | **kein statischer Fehler** |
| Doppelte Constraint- oder Indexnamen | **0** |
| Einfache Quotes/Klammern und terminierte Statements | **kein statischer Fehler** |

**Dateien:** `docs/DATABASE_CUTOVER_PHASE_B_01_CORE_DRAFT.sql` (71 Statements); `docs/DATABASE_CUTOVER_PHASE_B_02_PROGRESS_MEDIA_DRAFT.sql` (63 Statements); `docs/DATABASE_CUTOVER_PHASE_B_03_MEDIA_DETAILS_DRAFT.sql` (16 Statements).

**Fehler:** Keine in den oben beschriebenen begrenzten statischen Regeln.

**Pflicht vor Gate B / C:** Echte PostgreSQL-Integration in isolierter Datenbank, negative FK/CHECK/UNIQUE-/NoMatch/Progress-Commit-Tests, EF-Snapshot, festgelegte Byte-Enums/Seeds und repräsentative EXPLAIN-Planprüfung. Keine Alt-Datenbank verändern.
