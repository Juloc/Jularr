# Phase B — gemeinsames Monitoring und Artist-Commands

Isolierter Zielvertrag des vorhandenen Features/Monitoring-Owners. Keine Runtime-
Portierung, kein Store, kein zweites Wanted und keine Music-eigene Queue.

## Persistenz und Reichweite

WorkMonitoringDecisions speichert ausschließlich explizite Zielentscheidungen.
Abwesenheit bedeutet Inherit. Root-Off schlägt jede Relation; Kinder behalten
ihre eigenen Entscheidungen. Effective und All/Future sind keine persistierten
Modi. WorkMonitoringSources folgt genau einer typisierten Relation: Person,
normalisiertem Studio, Collection oder Artist. Vier feste byte/smallint-Codes;
keine polymorphe String-ID oder aus kopierten Labels rekonstruierte Identität.

Person-, Artist- und Collection-Targets haben reale bigint-FKs. Collection und
AddedByProfileId müssen dieselbe private Collection-Scope bilden. AddedByProfileId
ist Provenienz, keine neue Privatbibliothek oder automatische Berechtigung.
Studio ist der vorhandene externe Metadatenname, lower/trim-normalisiert. Nur
Personen besitzen einen optionalen Rollenfilter: 1–20 eindeutige normalisierte
Namen ohne NULL/Leerwerte. WorkCredits unterscheidet festen Rollentyp und echten
Provider-Rollennamen; Writer und Director derselben Person bleiben getrennte
Credits. Keine künstliche fixe Enum für beliebige Provider-Career-Namen.

Der statische EFFECTIVE-READ akzeptiert höchstens 100 genaue Work-UUIDs, prüft
aktuellen enabled Owner und Profile-Membership und löst alle vier Relationen in
einem SELECT auf. Root-Entscheidung zuerst, Relation nur bei Inherit. Rückgabe
enthält Work-PublicId und den abgeleiteten bool, keine private Quellenliste.
Die Owner-Grenze entspricht dem bestehenden Monitoring-API; D muss weitere
Consumer-Projektionen ausdrücklich an ihre tatsächlichen Rechte binden.

## Atomarer Artist-Command und Replay

[Statische Queries](DATABASE_CUTOVER_PHASE_B_MONITORING_QUERIES_DRAFT.sql) binden
Account/Profile/Operation als long, Artist/Command als UUID, bools und UTC-Zeit.
Service prüft DTO/Capability, Logic führt ausschließlich diese kurze Sequenz in
einem gemeinsamen READ COMMITTED-Tx aus:

1. BEGIN_ARTIST prüft enabled Owner/Membership und sperrt Account, Membership und
   Artist. Es erzeugt einen namespaced Idempotency-Beleg in bestehenden Operations.
2. Nur bei neuem Beleg: RESET_ARTIST entfernt bekannte Album-Root-Entscheidungen,
   nicht Edition-/Track-Entscheidungen.
3. SOURCE_ON oder SOURCE_OFF schreibt die einzige gemeinsame Relation-Absicht.
4. FUTURE_ARTIST setzt bei Follow + OnlyFuture alle jetzt bekannten Alben Off.
   All hinterlässt Inherit; None entfernt die Quelle. None + OnlyFuture ist ungültig.
5. COMPLETE_ARTIST markiert genau den Beleg Succeeded; erst dann COMMIT.

Eine Ausnahme oder Cancellation rollt Beleg und jede Änderung gemeinsam zurück.
Running wird nie separat committed; keine externe I/O in diesem Tx. Eine bereits
vergebene Command-UUID führt niemals erneut RESET/FUTURE aus. RECEIPT bestätigt
nur denselben aktuell autorisierten Actor/Profile/Artist/Flag-Payload mit einem
Succeeded-Beleg. Abweichendes Payload ist Konflikt, keine stille Umdeutung.
Die Zeit gehört nicht zum Replay-Payload. Operations-Retention muss die spätere
zugesicherte Retry-Frist erhalten (B12), nicht frühzeitig ihre Belege löschen.

Metadata-Logic veröffentlicht neue WorkMusicArtists-Mitgliedschaften unter
demselben Artist-FOR-UPDATE-Mutex. Bei mehreren Artists werden Locks nach Id
aufsteigend genommen. Remote-Metadaten werden vorher ohne DB-Tx geladen.
Damit ist jedes Album entweder vor dem Future-Snapshot bekannt und Off oder
danach neu und erbt Follow. Ein Retry darf spätere Alben nicht nachträglich Off
setzen. Neue absolute Commands sind seriell; Source-/Decision-State wird nicht
mit einem konkurrierenden gespeicherten Mode kombiniert.

## Nachweise und verbleibende Arbeit

Lifecycle/Placement prüfen Autorisierung, alle vier Quellen, mehrfache echte
Credits, Root-Off, All/None/Future, exaktes Release/Multidisc, NULL-Recording,
Rollback, falsche Composite-FKs und begrenztes 1000er Paging. Ein Paralleltest
beweist mittels pg_blocking_pids die tatsächliche Überlappung von neuem Command,
identischem Retry und späterem Metadata-Publisher: ein Gewinner, ein Receipt,
keine Off-Entscheidung am späteren Album und erhaltene Track-Entscheidung.

Allgemeine Work/Season/Volume/Edition-/Relation-Commands sowie After-Commit-
Follower, Request-Policies und Wanted-Reconciliation bleiben B08/B12-Aufgaben.
Der vorhandene gemeinsame Follower wird nach Commit geweckt; durable Effekte
gehören in den gemeinsamen Operations/Events-Pfad, nicht in eine Artist-Queue.
D/E portiert die Verbraucher; F prüft reale Adapter/Playback/E2E. Keine dieser
späteren Abnahmen wird aus grünen isolierten SQL-Tests abgeleitet.
