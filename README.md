# Jularr Backend

Backend-Neustart: gemeinsames Framework, Account-/Auth-/Session-Basistabellen,
genau ein Profile-Read und ein Profile-Update. Kein Frontend, kein EF, keine alten Feature-Bereiche.

Arbeitsregeln: [AGENTS.md](AGENTS.md), Code-/SQL-Konventionen:
[CONVENTIONS.md](docs/CONVENTIONS.md), Navigation: [CODEMAP.md](docs/CODEMAP.md).

- `Data.User.Profile.V1`: öffentliche Parameter, Eingaben und Ergebnisse.
- `Data.Logic.<Entity>`: neutrale Logic-Daten; `Data.Fields.<Entity>`: Felddefinitionen;
  `Data.Enums.<Entity>`: zugehörige Enums.
- `Service.User.Profile.V1`: Permission, zusätzliche Zugriffsprüfung, `OnRead`/`OnUpdate`.
- `Logic.Profile`: Laden mit Sperre, Geschäftsvalidierung und transaktionaler Writer.
- `Infrastructure.Sql`: parametrisierte SQL-Objekte und gemeinsame Connection/Transaction.
- `database/Account.sql`: zusammenhängende Account-/Auth-/Session-Baseline; [Umfang](database/README.md).

Das Framework registriert Services gemeinsam über `AddJularrServices` und
`MapJularrServices`. Nur mit `[ServiceEndpoint("GET", "/api/...")]` markierte
Klassen werden veröffentlicht; System-Services bleiben intern. Es gibt keine
Profile-spezifischen Registrierungs- oder Endpoint-Klassen. Parameter-/Body-Binding,
Antiforgery und bereinigte `ProblemDetails` laufen zentral. Das Attribut ersetzt
nicht die Berechtigungsprüfung. ASP.NET-Binding und Fehlerbehandlung verwenden
die [offiziellen Framework-Mechanismen](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0).

Typisierte Daten erben von `BaseData`; `BaseDataList<T>` bindet JSON-Listen.
Feldklassen definieren PostgreSQL-Typ, Pflichtwert und gegebenenfalls Länge.
Die gemeinsame Prüfung läuft vor Service-Ausführung und SQL-Binding; Logic
prüft Geschäftsregeln. Profile liefert Locale als `UiLocaleId` (`long`).
`BaseData.SetData(params BaseData[])` übernimmt vorhandene Eingaben in beschreibbare
Properties mit identischem Namen. Es gibt keine automatische Umbenennung anhand der
Felddefinition. Weggelassene Eingaben bleiben unverändert; explizites null wird geprüft.
Listen werden über ihre `Id` zugeordnet, nicht per Position. Unterschiedliche Namen
werden im Service ausdrücklich zugewiesen:

```csharp
updateData.Id = Parameters.ProfileId;
updateData.SetData(Data);
```

`GuidField.Required` verbietet null; `AllowEmpty` betrifft separat `Guid.Empty`.
Session-Kennungen dürfen nicht leer sein, WebAuthn-AAGUID darf den Null-Guid enthalten.
`UiLocales` ist der UI-Sprachkatalog (z. B. `de-DE`); Profile speichern dessen `UiLocaleId`.
Übersetzungen sind damit noch nicht implementiert.

Services bekommen keine Runtime im Konstruktor; die gemeinsame Registrierung
bindet sie einmal. Direkte Aufrufe verwenden `runtime.Create<TService>()`.
`PublicService` ergänzt User/Admin/System für Aufrufe ohne vorausgesetzte Session;
dies ist ein Service-Bereich, keine vierte Account-Rolle. Unbekannte geschützte
Permissions sowie noch nicht konfigurierte Admin-/System-Zugänge bleiben abgelehnt.

SQL steht unmittelbar in der ausführenden Methode in `sql.Statement`.
Parameter/Daten werden mit `sql.Parameters.Add(...)` gebunden. Die Logic-Aufrufe
stehen direkt auf `Context`: `ReadProfileForUpdateAsync` → `SetData` → `UpdateProfileAsync`.
Ressourcen-IDs sind intern und extern bigint/long. Session-/Token-IDs sind davon getrennt.

`ReadProfileV1` verwendet `ReadProfileParametersV1` und liefert `ReadProfileResultV1`
mit eigener `Id`. `UpdateProfileV1` verwendet `UpdateProfileParametersV1` und
`UpdateProfileDataV1`; ein gemeinsamer Writer ändert `DisplayName` und `UiLocaleId`
und liefert `UpdateResult`. Parameter heißen vollständig `ProfileId`.
Lesen verlangt die aktive Account/Profile-Zuordnung,
Ändern zusätzlich Profile-Eigentum. Keine Admin-Abkürzung und kein unbekannter Grant.
Die zentrale Session-Prüfung liefert die Auth-IDs; der Service prüft das angefragte
Profil gegen `Auth.ProfileId` und bei Änderungen dessen Eigentümer. Entity-SQL
wiederholt die Session-/Mitgliedschaftsprüfung nicht.
Nicht konfigurierte Module werden abgelehnt; die beiden Profile-Services benötigen keine.

## Start und Tests

`JULARR_DB` muss explizit gesetzt werden. Der Host erstellt oder migriert keine
Datenbank. Schema-Installation erfolgt nur bewusst in einer frischen Ziel-Datenbank.
Die beiden Routen sind `GET /api/v1/profiles/{profileId:long}` und
`PUT /api/v1/profiles/{profileId:long}`; Update benötigt den Antiforgery-Token aus GET.

`dotnet build Jularr.sln` baut das Backend. `dotnet test Jularr.sln` benötigt
`JULARR_TEST_DB` mit einem isolierten PostgreSQL auf `127.0.0.1:15433`, Benutzer
`jularr_test`, Administrationsdatenbank `jularr_test_admin`. Tests erstellen nur
eigene zufällig benannte Datenbanken; App-Verbindungen werden abgelehnt.

## Bewusste Integrationsgrenze

Die Session-Prüfung erwartet geschützte Cookie-Claims `jularr.session_id` und
`jularr.session_revision`, prüft den tatsächlichen AccountSessions-Zustand und
sperrt ihn über `Logic.Session` erneut in der Schreibtransaktion. Die Session-UUID
ist kein Bearer-Secret. Es gibt noch keine DB-validierende Auth-Middleware;
die Prüfung läuft derzeit im gemeinsamen ServiceGate.
Login/Session-Ausstellung, sämtliche bisherigen Razor-/Android-/TV-Verbraucher,
weitere Profile-Funktionen und Profil-Einstellungen sind noch nicht umgestellt.
Der isolierte Backend-Neustart ersetzt keine aktive Installation und schließt Phase B nicht ab.
