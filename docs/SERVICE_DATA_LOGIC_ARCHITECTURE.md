# Service / Data / Logic / SQL — verbindlicher Jularr Backend-Clean-Cut

**Status: Zielarchitektur, NICHT implementiert (10.10.2026).**

**Verbindlicher DB-Cutover-Ausführungsplan:** [DATABASE_CUTOVER_EXECUTION_PLAN.md](DATABASE_CUTOVER_EXECUTION_PLAN.md), überwacht in #880. Runtime-Fundament #942 darf vor der finalen Baseline vorbereitet werden; schemaabhängiges SQL wird zuerst gegen die isolierte PostgreSQL-Ziel-Baseline implementiert/getestet. Bestehende dev-Datenbank erst im zuletzt gesondert freigegebenen destruktiven Cutover umschalten. Diese Datei ist die maßgebliche, konsolidierte Spezifikation für Backend-Servicegrenzen, DTO-Namespace, Logic-Mutationen und SQL-Transaktionen. Sie supersediert ältere widersprechende Zielcode-Beispiele in #852, #930 und anderen Issues. Die bestehende dev-Laufzeit und die aktuelle PostgreSQL-Datenbank bleiben bis zu einem **separat freigegebenen, koordinierten Cutover** unverändert. Der freigegebene Datenbank-Zielzustand steht ausschließlich in [CLEAN_CUT_DATABASE.md](CLEAN_CUT_DATABASE.md); allgemeine SQL-Regeln und aktuelle Helper in [DATABASE_CONVENTIONS.md](DATABASE_CONVENTIONS.md), Wartbarkeit in [MAINTAINABILITY_CONVENTIONS.md](MAINTAINABILITY_CONVENTIONS.md), C#-Stil in [CODING_CONVENTIONS.md](CODING_CONVENTIONS.md). Verwandte Issues: #852 Backend-Umbrella, **#942 Backend-Fundament mit konkreten Implementierungs-/PostgreSQL-Tests**, #880 Datenbank-Baseline, #930 UI, #925 WorkCards, #853 Fehler.

## 1. Rollen, Layer und exakte Abhängigkeiten

| Ebene | Namespace | Besitzt | Darf NICHT |
| --- | --- | --- | --- |
| Versioniertes Data | `Jularr.Data.User.<Entity>.V1` / `Admin` / `System` | V1-`Parameters`, Input-`Data`, Output-`Result`, Sort-/Filter-Enums | SQL, Businesslogik, DB-Verbindungen |
| Internes Data | `Jularr.Data.<Entity>` | Rollen- und versionsneutrale Change-/Domain-Objekte | Transport-/Rollenvertragsabhängigkeiten |
| Service | `Jularr.Service.User.<Entity>.V1` / `Admin` / `System` | Autorisierte Entrypoints, Gate-Metadaten, Read-SELECT-Projektion, Workflows aus mehreren Logic-Aufrufen | INSERT/UPDATE/DELETE/UPSERT, SELECT FOR UPDATE, mutierende SQL-Funktionen/CTEs, Filesystem-/Provider-Seiteneffekte |
| Logic | `Jularr.Logic.<Entity>` | Alle fachlichen Mutationen und Seiteneffekte, Invarianten, atomare SQL-Schreibzugriffe, nicht-DB-Aktionen, durable Workflows | User/Admin/System-Aufteilung, V1-DTOs, UI/HTTP, unabhängiger Commit in laufender Service-Transaktion |
| Infrastructure.Sql | `Jularr.Infrastructure.Sql` | EIN NpgsqlDataSource-Pool, neutraler SqlContext, Typbindung, Read-/Write-Fassaden, Cancellation, Timeouts | Permission-/Businesslogik |
| Razor/API | Web/UI | Presentation + dünne HTTP-Adapter; ausschließlich Service über DI/HTTP | DB/Logic/Store/Katalog/Scanner/Provider direkt |

**Richtung:** Data.Common und Infrastructure sind neutral. `Logic` referenziert interne Data und neutrale Infrastructure, **niemals Service** oder versions-/rollenspezifische Data. `Service` referenziert Data und Logic. UI/Web referenziert Service und versionsspezifische DTOs, nicht Logic. Das verhindert zirkuläre Assembly-Abhängigkeiten. Echte .NET-Projektgrenzen dürfen pragmatisch minimal bleiben, die Namespace-/Call-Verbote gelten trotzdem und benötigen Architekturtests.

**Keine künstliche Store-Schicht:** `Logic` ist der vereinbarte allgemeinere Name (auch Datei-/Netzwerk-/Job-Arbeit). Rollenbezogene Read-SQL-Projektionen leben direkt im Read-Service; Mutation SQL und transaktionales `SELECT FOR UPDATE` nur in Logic. Keine Repository-Forwarder pro SELECT, kein generischer CRUD- oder SQL-Builder.

## 2. Dateistruktur (Entity-first, trotzdem Layer-getrennt)

Alle V1-DTO-Typen einer Entität und Rolle liegen **in einer einzigen Data-V1-Datei**, aber **getrennt von den Services**. Alle V1-Operationen derselben Entität/Rolle liegen **in einer einzigen Service-V1-Datei**. Interne Logic und Data besitzen weder Area noch API-Versionssuffix. Eine Dateigrenze entspricht einer **fachlich zusammenhängenden Entitätszuständigkeit**, nicht mechanisch exakt einer physischen SQL-Tabelle.

~~~text
Jularr.Data/
  Common/                               # IServiceOutput, NoData, PageRequest, PageResult<T>
  User/Watchlist/V1/Watchlist.cs        # alle Read/Update-Watchlist-V1-DTOs
  User/Accounts/V1/Accounts.cs          # ReadAccountV1, UpdateAccountV1 DTOs
  User/Profiles/V1/Profiles.cs          # ReadProfileV1, UpdateProfileV1 DTOs
  Admin/Accounts/V1/Accounts.cs         # eigene Admin-DTO-/Feld-Allowlist
  Admin/Library/V1/Library.cs
  System/Library/V1/Library.cs
  Accounts/Accounts.cs                  # interne, unversionierte Change-Objekte
  Watchlist/Watchlist.cs
  Profiles/Profiles.cs
Jularr.Service/
  Core/Service.cs, ServiceRuntime.cs, ServiceGate.cs, ServiceContext.cs
  Core/ServiceOperationType.cs, UserListService.cs, AdminListService.cs
  User/Watchlist/V1/Watchlist.cs        # ReadWatchlistV1 + UpdateWatchlistV1
  User/Accounts/V1/Accounts.cs          # ReadAccountV1, UpdateAccountV1
  User/Profiles/V1/Profiles.cs          # ReadProfileV1, UpdateProfileV1
  Admin/Accounts/V1/Accounts.cs         # Admin-Read und -Update
  Admin/Library/V1/Library.cs           # StartLibraryScanV1
  System/Library/V1/Library.cs          # ExecuteLibraryScanV1
Jularr.Logic/
  Watchlist/Watchlist.cs
  Accounts/Accounts.cs
  Sessions/Sessions.cs
  Profiles/Profiles.cs
  Library/Library.cs
  Operations/Operations.cs
Jularr.Infrastructure/
  Sql/SqlContext.cs, SqlExecutor.cs, SqlParams.cs
Jularr.Web/
  Api/...                               # dünne Operation-Adapter
  Pages/...                             # Razor nutzt DI-Service, kein Self-HTTP
~~~

`AccountId` adressiert eine Login-Identität, `ProfileId` eine persönliche Profilidentität. **Kein `ReadUserV1(AccountId)`** als mehrdeutige Account-/Profilfunktion; `ReadAccountV1`/`ReadProfileV1` sind sinnvolle Beispiele, keine automatisch einzuführenden Funktionen. Ein Owner/Admin verwendet User-Services auf normalen Seiten, Admin-Services nur im Admin-Bereich. System-Services nur durch vertrauenswürdige Worker/Operation-Claims. Keine Rollen in Logic-Namen; gemeinsame Domaininvarianten sind nicht pro Rolle dupliziert.

## 3. OperationType, Gates, Rückgabetypen und automatische Transaktion

Jede registrierte `Service<TParameters,TData>`-Operation besitzt folgende beim Startup validierte Metadaten:
- `GetOperationType()` liefert genau `Read`, `Create`, `Update`, `Delete` oder `Execute`. `GetServiceType` wird nicht verwendet: User/Admin/System ist bereits die **Area**, OperationType ist die **Art** der Aktion.
- `GetResultTypes()` gibt eine endliche Menge tatsächlich erlaubter erfolgreicher `IServiceOutput`-DTO-Typen mit **exakt einem Default**. Klassen-Generic `TResult` gibt es nicht. `ExecuteAsync<TOutput>` erlaubt nur bereits deklarierte Resulttypen; Clients können niemals beliebige CLR-Klassennamen bestimmen. Private Outputvarianten erfordern entsprechende zusätzliche Permission.
- `GetInstanceModules(parameters)` mit explizitem None/All/Any; dynamische Medienmodule ggf. nach kleiner autorisierter Resource-Metadatenprüfung; Modul-Aus stoppt Services und Worker, nicht bloß die Navigation.
- `GetPermission(parameters, resultType)` und `GetResource(parameters)` bzw. bei mehreren beeinflussten Ressourcen ein expliziter Multi-Target-Zugriffsvertrag. Jeder betroffene Target erhält einen Gate-/Race-Check. Kein primärer Account als pauschale Berechtigung für Profile, Sessions, LibraryRoots etc.
- `GetTransaction(parameters)` konfiguriert nur im erlaubten Rahmen das Isolation-Level. Bei Create/Update/Delete darf `null` keine stille Nicht-Atomizität eines Mehrschrittablaufes verursachen.
- Nur List-Services definieren geprüfte `GetSortKeys()` mit genau einem Default sowie Paging-/Filter-Metadaten.

**Zentraler nicht überschreibbarer Ausführungsweg:** erforderliche Module zuerst -> Parameter-/Result-Metadaten validieren -> Actor/Session/Profile/Worker oder eng definierte Auth/Public-Caller-Policy -> Permission/Resource-Scopes -> ein neutraler SQL-Kontext -> automatisch Modus und optionale Transaktion eröffnen -> race-sensitive Rechte wieder prüfen -> alle Logic-Schritte bzw. die reine Read-Abfrage awaiten -> Result prüfen -> **ein Commit durch die Service-Basis**. Bei Fehler/Cancellation Rollback/Dispose. Standardfehler werden zentral erzeugt/übersetzt, kein GetErrorTypes und keine manuellen Routine-NotFound-Throws.

| OperationType | Zugriff | Default-Transaktion |
| --- | --- | --- |
| Read | C# nur `ReadSql` UND PostgreSQL-Transaktion ausdrücklich `READ ONLY` | Kurz, ohne DML/Write-Locks/Seiteneffekte |
| Create / Update / Delete | Schreibzugriff ausschließlich in Logic | Eine kurze `READ COMMITTED READ WRITE`-Transaktion für alle Logic-Schritte, ggf. begründet anderes Isolation-Level |
| Execute | Nicht als Read einstufen; eigene ausdrücklich definierte Policy | Kurzer DB-Teil oder **keine** äußere Tx bei langen Jobs; bounded interne Tx-Batches |

Read-only ist **kein Boolean, der nur in C# behauptet wird**. PostgreSQL-Transaktion erhält wirklich den `READ ONLY`-Modus; tatsächliche Npgsql-Version/SET TRANSACTION/BEGIN-Kompatibilität prüfen, ohne Session-Einstellungen auf Pooled Connections durchsickern zu lassen. Auch Read-only SQL ist weiterhin statisch und geprüft: unerwünschte Seiteneffekte über SQL-Funktionen/Provider sind damit nicht pauschal ausgeschlossen. PostgreSQL `READ COMMITTED` liefert pro Statement einen Snapshot, nicht zwingend einen gemeinsamen Snapshot über mehrere Statements; `REPEATABLE READ` nur für konkret benötigte mehrteilige konsistente Reads. Read-Transaktionen kurz halten und Mehrkosten messen.

## 4. Dieselbe Connection und Transaction in Service und ALLEN Logic-Funktionen

**Ein SqlContext pro Serviceausführung** aus neutraler Infrastructure.Sql: lazily eine Connection aus anwendungsweitem `NpgsqlDataSource` und höchstens eine Service-eigene aktive `NpgsqlTransaction`. `ServiceContext` und `LogicContext` sind **zwei begrenzte Sichten auf denselben darunterliegenden SqlContext**, niemals eigenständige Connection/Tx-Inhaber. Read-Service erhält nur `context.ReadSql`, keine Write-Methode und keinen frei verfügbaren NpgsqlConnection-Zugriff. Logic-Funktionen erhalten den für die aktuelle Operation zulässigen `context.Logic` mit demselben SQL-Context und Write-SQL-Methoden. Die Basisklasse erzeugt den Modus, übernimmt Commit/Rollback/Dispose; kein Logic-eigener `OpenConnection`/`Commit`/`BeginTransaction` innerhalb des Service-Scopes. Keine Ambient-/AsyncLocal-Transaktionen, keine parallelen Commands/Reader auf derselben Npgsql-Connection, keine lange offene Tx über Netzwerk/NAS/Provider.

**Rollenabhängige Komposition, gemeinsame Domaininvarianten:** Ein User-Service darf z. B. nur den eigenen Anzeigenamen über `Logic.Accounts` ändern. Ein Admin-Service kann Account, Sessions, Audit in einer SQL-Tx koordinieren; jeder Logic-Aufruf sieht exakt dieselbe Connection/Tx. Fehlgeschlagener zweiter/dritter Aufruf rollt die vorherigen Änderungen zurück. Wenn jede Accountdeaktivierung **immer** einen Sessionwiderruf verlangt, dann ist das eine universelle Invariante und muss in `Logic.Accounts` (ggf. durch Aufruf von `Logic.Sessions`) durchgesetzt werden, **nicht** nur im Admin-Service. Der Service koordiniert ausschließlich optionale bzw. rollenabhängig andere Workflows; Domaininvarianten bleiben in Logic. GetPermission/Resource und alle beteiligten Targets müssen vor Mutation autorisiert sein.

**Nicht-DB-Seiteneffekte** (NAS, Netzwerk, Mail, Provider, Benachrichtigung, Queue außerhalb DB) sind nicht durch PostgreSQL rollbar. Dazu in der DB-Transaktion eine dauerhafte Operations-/Outbox-Absicht speichern und nach Commit idempotent verarbeiten. Keine falsche „eine Transaktion über alle Systeme“-Behauptung; System-Execute mit langem Scan benutzt kurze transaktionale Einheiten.

## 5. SqlExecutor und direktes vollständiges Parameters-Objekt

Normale Caller übergeben das komplette vorhandene typisierte DTO ohne manuelles `SqlParams.Create().Add().ToArray()` und ohne `new { ... }`:

~~~csharp
// Im lesenden Service:
return await context.ReadSql.ReadRequiredAsync<ReadAccountV1Result>(ReadSql, parameters, cancellationToken);

// In Logic, niemals als Service-Mutation:
await context.Sql.ExecuteAsync(UpdateSql, change, cancellationToken);
~~~

**Nur die im fixen statischen SQL referenzierten Platzhalter werden gebunden**, und zwar aus den typisierten DTOs plus gegen Überschreiben geschützten Actor-/Profile-/Paging-Werten aus dem Context. Ein DTO-Extra erzeugt keine SQL-Spalte, WHERE-Klausel oder ORDER BY. Fehlende Namen, Kollision parameters/data, Null-/Enum:byte->smallint-/Array-/jsonb-/citext-Typfehler werden abgewiesen. Binding-Pläne pro SQL+DTO-Shape einmal validieren/cachen; keine naive SQL-Regex, keine dynamische SQL-Generierung. Aktuell existiert der explizite `SqlParams`-Helper; die automatische Ganz-DTO-Übergabe ist **erst Zielcode**, keine bereits implementierte Funktion. Bestehende Typzuordnungen wiederverwenden, keinen zweiten inkompatiblen SQL-Helper bauen.

Read-Helper: `ReadRequiredAsync<T>` = genau eine Zeile, sonst zentral NotFound/Integritätsfehler; `ReadOptionalAsync<T>` = legitime Abwesenheit erlaubt; `ReadPageAsync<T>` = paginierte Items, leere Liste erlaubt. Versioniertes `ReadWatchlistV1Result(PageResult<WatchlistWorkV1> Page) : IServiceOutput` liegt in Data.User.Watchlist.V1, nicht im Service-File. Kein zweites Paging-Modell. Write-Helper/RETURNING und `SELECT FOR UPDATE` nur in Logic. **0 betroffene UPDATE-Zeilen ≠ automatisch NotFound**, sondern ggf. Conflict/Version/Scope. Zentrale Fehlermeldungen nach #853 ohne SQL/Tokens/Stacks an Clients.

## 6. Outward SELECT, Sort/Paging und WorkCards

Jede potenziell unbeschränkte externe Liste ist serverseitig paginiert: `Page` 1-basiert, Default 1; `PageSize` Default 25, Max 100; validiertes Offset (bestehender Grenzwert 100000). `ORDER BY` deterministisch mit eindeutigem ID-Tie-Breaker; Filter und Profile/Media-Rechte **in PostgreSQL vor Pagination**. Keyset nur als bewusstes gemessenes Alternativverfahren. `GetSortKeys()` ist typsichere Enum-Allowlist mit **genau einem Default**; pro Schlüssel ein eigenes vollständig statisches, überprüftes PostgreSQL-SELECT ohne zusammengesetzten SQL-Text. Explizite Locale/Fallback/NULL-Ordnung, passende Indexe und EXPLAIN (ANALYZE, BUFFERS).

Ein fachliches SELECT pro paginierter outward Rootliste einschließlich benötigter **bounded** Kindlisten: Root erst autorisieren/paginieren, dann unabhängige `LATERAL + jsonb_agg(jsonb_build_object(...))`-/Array-Aggregate; keine Cross-Products, kein N+1/zusätzliche Child-SELECTs, leere Kinder als `[]`, jedes Kind deterministisch sortiert und begrenzt. Unbeschränkte Unterlisten erhalten eigene paginierte Operation. `TotalCount`/`HasMore` nur korrekt ermitteln, nicht aus voller Seite erfinden. Watchlist/Library/Home arbeiten mit den lokalen DB-Fakten (#925) inklusive lokalisierten Work-Namen, Bild-IDs, Audio-/Subtitle-Languages, Fortschritt/Verfügbarkeit; keine live NAS-/Provider-Reads während einer UI-Abfrage.

## 7. User/Admin/System, API, UI und Jobs

- Normale Razor-Seiten nutzen ausschließlich `Service.User.*` auch für Owner/Admin-Accounts; `/Admin/*` ausschließlich `Service.Admin.*`. Razor C# ruft den Service via DI ohne Self-HTTP. Browser JavaScript und TV/Android können dünne JSON-HTTP-Adapter derselben Operation aufrufen. Eine HTTP-API existiert nur bei echtem Consumer, Version zuerst: `/api/v1/...` und `/api/v1/admin/...`. **Kein** UI-/API-Logic-/SqlContext-Direktaufruf.
- `Service.System.*` nur mit vertrauenswürdigem dauerhaften Worker-Claim, nicht direkt als HTTP-Endpunkt; Auth/Setup/Public/Streams mit ausdrücklich beschränkter Sonder-Caller-Policy, nicht durch gefälschte Sessions. CSRF bei Cookie-Mutationen und Budgets/Rate-Limits für externe Operationen.
- Admin-Scan mit `Queue` oder `Direct` und unabhängigem `Force`. Queue = durable enqueue; Direct = reservierter begrenzter Slot außerhalb normaler Reihenfolge, **kein langer HTTP-Request und kein Task.Run-Fire-and-Forget**; kein Slot => definierter Busy/Conflict-Fehler. Force darf nur bestimmte Freshness-Skips überspringen, keine Auth/Modul/Locks/Kapazität/Safety. EINE kanonische dauerhafte Operation-/Workerverwaltung, keine zusätzliche Queue; System-Scan durch Logic mit bounded SQL-Mutationsbatches und Restart/Cancel/Idempotenz.
- Modul deaktiviert => Service, Worker-Annahme, unnötiger Provider-/Cache-Init deaktiviert; laufende Arbeit kontrolliert abbrechen. UI-Komponenten (#930) nutzen registrierte Service-Sort-/Paging-Allowlist und die V1 DTOs aus Data, niemals direkte Logic-/Store-Aufrufe. Mehrere Lists mit eigenem Page-State und Deep-Link-Fortsetzung.

## 8. Verbindliche Umsetzungsgates und Reihenfolge

1. **Architektur-/Compile-Gates:** Data-V1 und Service-V1 je eine Entity-Datei pro Area; separate Data.<Entity> und Logic.<Entity> ohne V1/Area; kein zirkuläres `Logic -> Service`, kein `Web/API -> Logic`, keine schreibenden SQLs oder Seiteneffekte in Service. OperationType, GetResultTypes (genau ein Default), Modul-/Permission-/SortKeys-Metadaten beim Startup validieren.
2. **Echte PostgreSQL-Tests:** READ ONLY weist DML/FOR UPDATE ab und Read-Mode leakt nicht auf die nächste Pool-Nutzung; mehrere Logic-Schritte nutzen identische Connection/Tx; Failure bei Schritt 2/3 rollt alle SQL-Änderungen zurück; Scope-/Profile-/Account-/Ressourcenrechte auch bei Concurrency.
3. **SQL-DTO-Mapping/Performance:** ganzes Parameters/Data-Objekt, nur verwendete SQL-Parameter; TypeMapping null/enum/array/jsonb/citext/reservierte Werte, Strings/Quotes/Kommentare/Casts; bindungs-/pool-sicher; ein SQL-Statement mit verschachtelten sortierten Kindern, gemessene Query-Pläne auf repräsentativen PostgreSQL-Daten, Read-only-Tx-Overhead auf schwachem Server.
4. **Fachfälle:** Account vs Profile eindeutig, User-/Admin-Field-Allowlist, PATCH „nicht gesendet vs null vs Wert“, universal Domaininvarianten, Sessions/Permissions/Audit im gemeinsamen Tx. Queue/Direct/Force/Worker-Cancel/Restart; Client-/Frontend-/Admin-Consumer-Tests.
5. **Phasen:** (A) Regeln/neutraler SQL-Kontext/Runtime/Tests auf Feature-Branch, (B) separater Account-Read/Profile-Read, Watchlist mit zwei echten Sort-SELECTs, Admin-Update mit drei Logic-Aktionen, (C) System-Jobs und #930 UI-Bindings, (D) vollständiges altes Tabellen-/API-/Razor-/Android-/TV-/Worker-Inventar, (E) freigegebener DB-Clean-Cut #880. Andere offene PRs und laufendes dev nicht blind überschreiben.
6. **Keine voreilige DB-Migration:** Zielmodell ist Planung, keine Erlaubnis zum Schema-Reset. Alte Doc-Codebeispiele beschreiben teils **heute** existierenden `SqlParams`-/Store-Pfad und dürfen nicht als neue Servicearchitektur kopiert werden.

**Vorrang:** Diese Datei ist maßgeblich für die neue Service/Data/Logic-Architektur. `CLEAN_CUT_DATABASE.md` ist maßgeblich für DB-Struktur; `DATABASE_CONVENTIONS.md` für SQL-Format und aktuell implementierte Helper; `MAINTAINABILITY_CONVENTIONS.md` für allgemeine Wartbarkeit. Bei alten Service-/DTO-/Store-Pfad-Beispielen in diesen Dokumenten gilt die neuere spezifische Trennung hier. Alle Agenten müssen diese Fassung vor Backend-, Admin-UI- oder SQL-Cutover-Arbeit lesen und die Abhängigkeiten berücksichtigen.
