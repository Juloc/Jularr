# Phase B — ein atomarer Fortschrittsvertrag

Nur isolierter SQL-/Data-Zielvertrag, keine Runtime-Portierung oder aktive DB.
[Part 18](DATABASE_CUTOVER_PHASE_B_18_PROGRESS_READER_LIFECYCLE_DRAFT.sql) und
[statische Queries](DATABASE_CUTOVER_PHASE_B_PROGRESS_QUERIES_DRAFT.sql) ersetzen
die frühere alleinstehende Revision-CAS-Abfrage samt separatem Beleg-INSERT.
Es gibt keinen zweiten Offline-Reconciler oder Furthest-Position-Fallback.

## Identität und Schreibgrenze

Service löst den authentifizierten Account und das aktive Profil auf und
autorisiert. Data enthält die getypten Parameter/Ergebnisse; ausschließlich
Logic führt CHECKPOINT im kurzen SQL-Tx aus. Interne IDs/FKs sind bigint/long;
Progress-/Work-/Edition-Adressen sind separate UUIDs. ClientEventId ist ein
profilgebundener Idempotenzschlüssel, weder Ressourcen-FK noch Secret.

Ein neu gestartetes exaktes Target erhält in einem Tx den kanonischen
MediaProgress-Root (Revision 1) und genau seinen obligatorischen Detailtyp.
Die bestehende NULLS-NOT-DISTINCT-Unique verhindert parallele zweite Roots.
START ist ein ausdrücklicher Logic-Befehl, niemals ein GET-Nebeneffekt. Er löst
öffentliche Target-IDs innerhalb desselben Works auf, erzeugt Root und Detail
atomar und mutiert bei Unique-Kollision nichts. Anschließend liest TARGET die
bestehende Identität im frischen Snapshot; kein No-op-UPDATE als Read. Eine
parallele Neuanlage erzeugt weder zweiten Root noch verwaistes Detail. Die
PositionType-Auswahl kommt vom getypten Medien-/Format-Owner, nicht ungeprüft
aus dem Client. Ein vorhandener Root wird nicht in einen anderen Typ umgedeutet.

CHECKPOINT sperrt Account und aktuelle Membership, anschließend den exakten
Progress-Root. Enabled, Profile und ExpectedRevision werden vor jedem Write
geprüft. Eine fehlende/entzogene Membership bleibt auch bei Kenntnis der UUID
unberechtigt. Dieselbe Statement-Transaktion schreibt einen unveränderlichen
Beleg, erhöht die Revision einmal und ändert genau die Time-/Reading-/Game-
Position. Constraint-Fehler und Rollback hinterlassen keine Teilergebnisse.
Keine externe I/O und kein versteckter Worker-Aufruf im Tx.

Die 20 Parameter sind explizit gebunden: Account/Profile long, Progress/Event
UUID, ExpectedRevision long, PayloadHash byte[32], Source byte, Now/ClientTime
UTC-Zeitpunkte, CompletionReason byte?, ClearCompletion bool, Time-Werte long?,
Edition UUID?, ContentRevision int?, VariantKey string?, Locator geschlossenes JSON?,
ProgressPermille short?, PlayedMs long?, ProgressPercent decimal?. Ein Payload
mit Feldern eines anderen Positionstyps wird nicht übernommen.

## Replay und Konflikt

Logic berechnet SHA-256 über den kanonischen fachlichen Payload: Target-UUID,
ExpectedRevision, Source, ClientOccurredAt, CompletionReason/ClearCompletion
und sämtliche getypten Positions-/Inhaltsfelder. Deterministische Feldreihenfolge,
UTC-/Dezimal-/JSON-Normalisierung gehören zum Data-Vertrag in D. Server-Now ist
nicht Teil des Replay-Payloads; PayloadHash ist niemals eine ungeprüfte
Client-Angabe. Ein verlorener Erfolgs-Response wiederholt dieselbe Event-ID.
LiveClient/OfflineClient bezeichnet die Herkunft des ursprünglichen Checkpoints,
nicht den Transport eines späteren Retries. Ein Live-Event in einer Offline-
Retry-Queue behält denselben Source-Code und denselben kanonischen Payload;
die spätere Zustellung darf daraus keinen scheinbar anderen Event machen.
UserAction wird nur vom expliziten manuellen Owner autorisiert, nicht aus einem
ungeprüften Source-Feld der Offline-DTO übernommen.

Ein erneuter CHECKPOINT für ein erfasstes Event schreibt nichts. RECEIPT prüft
aktuelle Berechtigung, Profil, exakte Progress-UUID und denselben PayloadHash.
Nur dann gibt es AcceptedRevision und CurrentRevision zurück. Wiederverwendung
mit anderem Payload/Target ist Konflikt, kein erneuter Erfolg. Ein stale
ExpectedRevision überschreibt keine neuere Position, auch nicht bei weiterem
Zeit-/Seitenfortschritt. Eine Konfliktauflösung benötigt den aktuellen Snapshot
und einen ausdrücklich neuen Checkpoint. Replays setzen keinen alten Snapshot
als aktuelle Client-Anzeige ein; SNAPSHOT liefert nur unter aktueller
Berechtigung die exakten Positions- und Completion-Fakten. ClientOccurredAt bleibt Audit-Metadatum;
LastActivityAt kommt ausschließlich vom Server, nicht von der Geräteuhr.

Belege bleiben unverändert. Eine spätere Retention muss die unterstützte
Offline-/Replay-Frist samt Ablehnung älterer Events festlegen, bevor Belege
entfernt werden dürfen; kein stilles Wiederaufleben gelöschter Event-IDs.
Dieser Lifecycle-Nachweis gehört noch zu B12.

## Completion und Reader-Anker

PlaybackEnded (1), ReaderFinished (2) und UserMarked (3) sind explizite byte-
Verträge mit FK-Seeds. Seek, Scrub, Resume, 1000 Permille oder 100 Prozent sind
kein Completion-Nachweis. Das deklarierte natürliche Ende ist Time-/Reader-
gebunden; eine manuelle Markierung erfordert den UserAction-Owner. Game hat
keine automatisch erfundene Ende-/PlayedTime-Schwelle.

Completed speichert Grund, Zeitpunkt und Account. Weitere Checkpoints behalten
die ursprüngliche Provenienz bei. Nur eine ausdrücklich autorisierte manuelle
UserAction mit aktueller Revision darf Completion samt Provenienz entfernen;
Live-/Offline-Positionen können das nicht. Ein manueller Statuswechsel übernimmt
den aktuellen getypten Positionssnapshot unter derselben Revision statt die
Position anhand eines fehlenden Client-Felds auf null/0 zu löschen.

ReaderContent ist eine unveränderliche Edition-/Work-/optionale Chapter-
Revision mit verbindlichem VariantKey. Neue Bytes erhalten eine neue Revision;
alte Anker werden nicht umgebogen. Nur veröffentlichte Inhalte dürfen gebunden
werden; Edition-/Chapter-Targets und Content müssen zusammenpassen. Ungeöffneter
Progress darf ohne Content/Locator bestehen. Part 19 validiert drei geschlossene
Locator-Shapes gegen den genauen Index; PageIndex ist ausschließlich erzeugt,
kein zweiter Schreibparameter. Der [Reader-Vertrag](DATABASE_CUTOVER_PHASE_B_READER_CONTRACT.md)
schließt Varianten, Veröffentlichung und Annotationen physisch ab.

Continue autorisiert vor Root-LIMIT und lädt getypte Positionen nur für diese
Seite. Reader-Resume enthält öffentliche Edition und exakte Inhaltsrevision.
COMPLETION aggregiert in SQL den lückenlosen regulären Episoden-Präfix einer
autorisierten Serie; eine fehlende/unfertige frühere Episode stoppt ihn auch
bei späteren fertigen Episoden. Season 0, explizite Specials und nicht positive
Episodennummern werden ausgeschlossen. Es entsteht kein gespeicherter zweiter
CompletedThrough-Zustand und keine Whole-Library-Auswertung im Client.

## Physische Belege

- [Checkpoint-Tests](DATABASE_CUTOVER_PHASE_B_PG_PROGRESS_CHECKPOINT_TESTS.sql):
  alle drei Typen, Hash-Replay/Reuse, Edition/Revision/Quellseite, explizite
  Completion/Unmark, Rollback, Sharing/Entzug/Disable und 1000 gleich datierte
  Continue-Roots mit gebundenen Positions-Reads und EXPLAIN.
- [Completion-Tests](DATABASE_CUTOVER_PHASE_B_PG_PROGRESS_COMPLETION_TESTS.sql):
  echte Lücken, Specials, Season-Grenze, manuelles Wiederöffnen und Isolation.
- [Zwei PG-Sessions](DATABASE_CUTOVER_PHASE_B_PG_PROGRESS_CONCURRENCY_TEST.sh):
  unterschiedliche berechtigte Accounts schreiben dieselbe Revision; genau ein
  Gewinner, keine stale Position/Completion und kein verwaister Beleg.

Gate B insgesamt bleibt offen. Runtime-/Client-Portierung folgt in D/E.
