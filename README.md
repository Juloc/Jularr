# Jularr Backend

Minimaler Backend-Neustart: gemeinsames Framework, genau ein Profile-Read und
ein Profile-Update. Kein Frontend, kein EF, keine alten Feature-Bereiche.

- `Data.User.Profile.V1`: öffentliche Parameter, Eingaben und Ergebnisse.
- `Service.User.Profile.V1`: Permission, zusätzliche Zugriffsprüfung, `OnRead`/`OnUpdate`.
- `Logic.Profile`: Laden mit Sperre, Geschäftsvalidierung und transaktionaler Writer.
- `Infrastructure.Sql`: parametrisierte SQL-Objekte und gemeinsame Connection/Transaction.
- `database/Profile.sql`: frische Profile-/Account-Baseline aus den Phase-B-Verträgen.

SQL steht unmittelbar in der ausführenden Methode in `sql.Statement`.
Parameter/Daten werden mit `sql.Parameters.Add(...)` gebunden. Die Logic-Aufrufe
stehen direkt auf `Context`: `ReadProfileForUpdateAsync` → `SetData` → `UpdateProfileAsync`.
Interne Ressourcen-IDs bleiben bigint/long; öffentliche Ressourcen-IDs sind UUIDs.

Die Read-Funktion liefert `ProfileDetails`; der Update ändert nur `DisplayName`
und liefert `UpdateResult`. Lesen verlangt die aktive Account/Profile-Zuordnung,
Ändern zusätzlich Profile-Eigentum. Keine Admin-Abkürzung und kein unbekannter Grant.
Nicht konfigurierte Module werden abgelehnt; die beiden Profile-Services benötigen keine.

## Start und Tests

`JULARR_DB` muss explizit gesetzt werden. Der Host erstellt oder migriert keine
Datenbank. Schema-Installation erfolgt nur bewusst in einer frischen Ziel-Datenbank.
Die beiden Routen sind `GET /api/v1/profiles/{publicId}` und
`PUT /api/v1/profiles/{publicId}/name`; Update benötigt den Antiforgery-Token aus GET.

`dotnet build Jularr.sln` baut das Backend. `dotnet test Jularr.sln` benötigt
`JULARR_TEST_DB` mit einem isolierten PostgreSQL auf `127.0.0.1:15433`, Benutzer
`jularr_test`, Administrationsdatenbank `jularr_test_admin`. Tests erstellen nur
eigene zufällig benannte Datenbanken; App-Verbindungen werden abgelehnt.

## Bewusste Integrationsgrenze

Die Session-Prüfung erwartet geschützte Cookie-Claims `jularr.session_id` und
`jularr.session_revision`, prüft den tatsächlichen AccountSessions-Zustand und
sperrt ihn erneut in der Schreibtransaktion. Die Session-UUID ist kein Bearer-Secret.
Login/Session-Ausstellung, sämtliche bisherigen Razor-/Android-/TV-Verbraucher,
weitere Profile-Funktionen und Profil-Einstellungen sind noch nicht umgestellt.
Der isolierte Backend-Neustart ersetzt keine aktive Installation und schließt Phase B nicht ab.
