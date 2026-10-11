# Phase B — Reader-Inhalte und Annotationen

Isolierter Zielvertrag, keine bestehende Runtime-Portierung oder aktive DB.
[Part 19](DATABASE_CUTOVER_PHASE_B_19_READER_CONTENT_DRAFT.sql) erweitert den
kanonischen ReaderContent-Owner; kein Book-/Manga-/PDF-Parallelmodell.

## Identität, Payload und Veröffentlichung

Interne Content-/Node-/Page-/File-/Operation-Referenzen sind bigint/C# long.
Öffentlich identifiziert Edition-UUID + optionale Chapter-UUID + VariantKey + ContentRevision genau eine
Revision. NULL Chapter bezeichnet nur den gesamten Editionsinhalt, nicht ein
beliebiges Kapitel. Der NULLS-NOT-DISTINCT-Schlüssel erlaubt Revision 1 pro
Kapitel und verhindert doppelte Vollinhalte. Original, offizielle und generierte Übersetzung besitzen feste byte-
Codes. Sprache, Richtungen und Schreibmodus werden explizit normalisiert; keine
alten int-Codes oder LegacyTranslation-Variante in der neuen Baseline.

Der normalisierte unveränderliche Text-/Asset-Payload liegt im eigenen `/data`-
Reader-Speicher, nicht nochmals als Volltext/Blob in SQL. StorageKey ist ein
opaker Schlüssel ohne Pfadsyntax. SHA-256, Parser-/Builder-Version und Counts
identifizieren den geprüften Payload. ReaderContentNodes hält nur seinen stabilen
Navigations-/Ankerindex; CharacterCount zählt UTF-16-Codeeinheiten wie C#/JS.
Text, Bilder und Parser-spezifische Payloads sind kein offenes SQL-EAV.

Parser, Dateizugriff und Übersetzung laufen außerhalb des DB-Tx unter dem
gemeinsamen Operations-Owner (`reader_parse` / `reader_translate`). Ein Builder
erfasst bekannte Counts und staged den Index set-basiert. PUBLISH sperrt exakt
seine aktuelle Operation, prüft Hash/Kind/Lease und veröffentlicht einmal. Die
Commit-Constraint verlangt den vollständigen dichten 0-basierten Index und
gültige physische Quellseiten. Drafts sind in keinem outward Content-/Page-/Node-
oder Delivery-READ sichtbar. Ein GET startet weder Parser noch Übersetzung.

Metadaten, Payload und veröffentlichte Indexeinträge sind unveränderlich. Neue
Bytes erhalten eine neue Inhaltsrevision; Fortschritt/Annotationen werden nicht
auf eine andere Revision umgebogen. Index-Mutationen sperren den Parent auch bei
DELETE, damit Veröffentlichung und Indexänderung nicht aneinander vorbeilaufen.
DELETE einzelner veröffentlichter Einträge scheitert am vollständigen Index.
Ein expliziter Graph-Delete im selben Tx ist nur ohne lebende Anker/Derivate
zulässig; keine Cascade und keine unbestimmte Retention-Frist.

Generierte Varianten referenzieren eine bereits veröffentlichte Quellrevision
desselben Work/Edition/Chapter. SourceNodeId und SourceReaderContentId bilden
eine echte zusammengesetzte FK; Quellanker gehören genau zum Parent-Payload.
Optionaler Original-File-Nachweis bindet StoredFile/Version/Edition/Work ebenso
zusammengesetzt. Keine vom Client erfundene Pfad- oder Source-Identity.

## Drei geschlossene Locator-Shapes

| Layout | Pflichtfelder | Optionale Felder / Grenze |
| --- | --- | --- |
| ReflowableText | blockId, characterOffset | fallbackPercent; vorhandener Textknoten, UTF-16-Offset 0..CharacterCount |
| ImageSequence | pageIndex | viewport, fallbackPercent; tatsächliche Seite dieser Revision |
| FixedPages | logicalPageIndex, sourcePageIndex | sourceRegion, viewport, fallbackPercent; exaktes logisches/physisches Seitenpaar |

Keine unbekannten Schlüssel, stringförmigen Zahlen oder beliebigen JSON-Objekte.
Viewport x/y liegen in 0..1, Zoom ist positiv und double-endlich; Regionen sind
positive Rechtecke vollständig innerhalb 0..1. fallbackPercent liegt in 0..100.
ReadingProgressPositions speichert nur Locator; PageIndex wird daraus erzeugt
und ist kein zweites schreibbares Positionsfeld. Ein ungeöffneter Root darf ohne
Content/Locator bestehen. Jeder tatsächliche Anker verlangt PublishedContent.

## Bookmarks und Highlights

Bookmarks behalten Label und gültigen Locator. Highlights sind echte, nichtleere
geordnete Reflow-Textbereiche; Bild-/Fixed-Page-Highlights werden nicht erfunden.
Service extrahiert SelectedText aus dem exakten immutable Payload, validiert
UTF-16-Grenzen und die bisherige Grenze von 2000 Zeichen; niemals Clienttext als
nachgewiesene Auswahl übernehmen. Note hat dieselbe 2000-Zeichen-Grenze.
Berechnete Positionen/int8range sichern Nichtüberlappung per Profile/Content auch
gegen konkurrierende Inserts. Sie werden ausschließlich vom Ankertrigger erzeugt.

[Logic-Writes](DATABASE_CUTOVER_PHASE_B_READER_ANNOTATION_QUERIES_DRAFT.sql)
sperren den enabled Account und aktuelle Membership. Create bindet die genaue
Edition/Kapitel/Variante/Revision, Edit/Delete benötigen zusätzlich ExpectedRevision.
Edit behält Identität/Profil/Content und erhöht genau einmal die Revision.
Create-Kollisionen überschreiben nichts; ein frischer autorisierter Einzel-READ
prüft bestehenden Payload/Target. Gleiche UUID mit anderem Payload ist Konflikt,
kein scheinbarer erfolgreicher Retry. UUID-Kenntnis ist keine Berechtigung.

## Reads und physische Belege

[Content-/Delivery-READs](DATABASE_CUTOVER_PHASE_B_READER_CONTENT_QUERIES_DRAFT.sql)
prüfen aktuelle Membership; nur der interne Delivery-Adapter bekommt StorageKey.
Clientheader/Annotationen enthalten keine internen IDs oder privaten Keys.
Bookmarks/Highlights nutzen PageRequest (1/25/max100/Offsetmax100000).
Pages/Nodes sind die gemessene immutable Sequential-Feed-Ausnahme: Startordinal
0, Size25/max100, eindeutiger innerhalb der Revision UNIQUE-Ordinalindex und
LIMIT in PostgreSQL. Autorisierung erfolgt einmal vor dem Feed. Source-Enrichment
betrifft nur die begrenzte Seite, keine Anwendungsfilterung oder N+1-I/O.

[PG-Tests](DATABASE_CUTOVER_PHASE_B_PG_READER_CONTENT_TESTS.sql) prüfen alle
Layouts, Varianten, Draft-Isolation, exakte Quellanker, CAS/Replay, Quoten,
fremde Accounts, Sharing/Entzug/Disable, Rollback und einen 1000-Knoten-Feed mit
EXPLAIN. [Drei PG-Sessions](DATABASE_CUTOVER_PHASE_B_PG_READER_CONCURRENCY_TEST.sh)
prüfen genau einen Publisher und abgewiesene konkurrierende späte Index-Writes.
[Kapitel-Regressionen](DATABASE_CUTOVER_PHASE_B_PG_READER_CHAPTER_TESTS.sql)
prüfen zwei Kapitel mit derselben Revision/Variante, NULL-Vollinhalt ohne Fanout,
exakte Content-/Node-/Page-/Delivery-/Annotation-Reads und Cross-Chapter-Ablehnung
im kanonischen Checkpoint sowie bei direktem relationalem Write.
Runtime-Parser/Delivery/Client-Portierung gehört in D/E, echte Ende-zu-Ende-
Abnahme in F. Music-/AI-Fachpersistenz und Replay-/Artifact-Retention bleiben
eigene offene B-Gates; dieser Reader-Nachweis schließt nicht Phase B insgesamt.
