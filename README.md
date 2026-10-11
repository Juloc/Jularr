# Jularr clean start

Ein ASP.NET-Host: `Jularr.Frontend` startet Razor Pages und API.
`Jularr.Backend` ist eine referenzierte .NET-Klassenbibliothek mit Services, Logic,
Data und PostgreSQL-Zugriff. Kein alter Web-Bereich, kein EF.

Regeln: [AGENTS.md](AGENTS.md), [CONVENTIONS.md](docs/CONVENTIONS.md).
Navigation: [CODEMAP.md](docs/CODEMAP.md).

## Backend im gemeinsamen Host

Das Frontend braucht eine ProjectReference auf `../Jularr.Backend/Jularr.Backend.csproj`.
Die Host-Anbindung ist noch nicht in dessen `Program.cs` eingebaut; dieser Bereich
bleibt beim Frontend. Zusätzlich zu Razor, Cookie-Authentication und Authorization:

```csharp
using Jularr.Backend.Service.Core;
using Npgsql;

var connectionString = builder.Configuration["JULARR_DB"]
    ?? throw new InvalidOperationException("JULARR_DB must be configured explicitly.");
builder.Services.AddJularrServices(
    new NpgsqlDataSourceBuilder(connectionString).Build(), typeof(ServiceRuntime).Assembly);
builder.Services.Configure<AuthenticationOptions>(builder.Configuration.GetSection("Authentication"));
```

Nach `Build()`: `app.UseExceptionHandler()` für den registrierten zentralen Handler,
`app.UseRouting()`, `app.UseRateLimiter()`, `app.UseAuthentication()` und
`app.UseAuthorization()`; dann `app.MapJularrServices()` neben `app.MapRazorPages()`.
Keine zweite Cookie-Registrierung. Backend ergänzt die vorhandene Cookie-Konfiguration
um DB-validierende Events, Secure/HttpOnly/SameSite und deaktivierte Sliding Expiration.
Der Host muss Data-Protection-Schlüssel dauerhaft und geschützt verwalten.
Keine automatische Schema-Installation oder Migration; [Baseline](database/README.md).

Razor ruft per DI dieselben Services direkt auf, beispielsweise
`await login.ExecuteAsync<LoginResultV1>(new LoginDataV1(email, password))`.
Nach erfolgreichem Auth-Aufruf übernimmt
`await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery)`
die Cookie-/Challenge-Effekte. Keine eigene Passwortprüfung und kein HTTP zum eigenen Backend.
Razor-POSTs brauchen weiterhin Antiforgery; die öffentliche Login-Seite sollte die
registrierte Rate-Limit-Policy `public` verwenden. API-Endpunkte deklarieren das selbst.

## Verfügbare Services

Alle öffentlichen Verträge liegen unter `Jularr.Backend.Data.<Area>.<Feature>.V1`,
Services entsprechend unter `Jularr.Backend.Service.<Area>.<Feature>.V1`.
Parameter erben von `BaseParameters`, Antworten von `BaseResponse`, Eingaben und
neutrale Logic-Daten von `BaseData`. Leere Parameter können bei direkten Aufrufen
entfallen; erforderliche Ressourcen-IDs bleiben ausdrücklich erforderlich.
SQL steht unmittelbar in `sql.Statement`; Logic teilt Connection und Transaction.

| Route | Verhalten |
| --- | --- |
| `GET /api/v1/auth` | Auth-Zustand, Plex-Flags und Antiforgery-Header |
| `POST /api/v1/auth/login` | E-Mail/Passwort, persistente DB-Session und geschütztes Cookie |
| `POST /api/v1/auth/register` | Deaktivierte Registrierungsanfrage mit eigenem Profile |
| `POST /api/v1/auth/plex` | Browsergebundener Plex-PIN-/JWT-Versuch und AuthorizationUrl |
| `POST /api/v1/auth/plex/{authChallengePublicId:guid}` | Bestätigung abfragen; Session oder Freigabeanfrage |
| `GET /api/v1/profiles/{profileId:long}` | Aktives, zugeordnetes Profile lesen |
| `PUT /api/v1/profiles/{profileId:long}` | DisplayName/UiLocaleId über den kanonischen Writer ändern |

API-Mutationen brauchen Cookie und `RequestVerificationToken` aus dem GET-Header.
Nach Login den erneuerten Header verwenden. Plex-Abfragen nicht häufiger als alle
fünf Sekunden senden. Ein Versuch läuft nach fünf Minuten ab und ist nur einmal abschließbar.
HTTP-Fehler und Feldvalidierung werden zentral als bereinigte ProblemDetails ausgegeben.
Session-/Plex-Geheimnisse erscheinen nicht im JSON; die DB speichert Session-Secret-Hashes.

Die zentrale Middleware prüft Session-Secret, Ablauf, Widerruf, CredentialRevision
und Account/Profile-Zuordnung. Schreibzugriffe prüfen und sperren den aktuellen
Zustand erneut in derselben Transaktion. `profile.self.update` verlangt Eigentum
im gemeinsamen Permission-Gate; im Profile-Service steht nur der Abgleich mit `Auth.ProfileId`.
Profile-PINs werden nicht durch automatische Auswahl umgangen: Login kann zunächst
eine Account-Session ohne aktives Profile erstellen.

## Plex-Konfiguration

Plex ist standardmäßig aus. Konfiguration unter `Authentication`:

- `SessionLifetimeHours`: 24, zulässig 1–720.
- `PlexLoginEnabled`, `PlexRegistrationEnabled`: jeweils false.
- `PlexRequireApproval`: true; neue Plex-Accounts bleiben bis zur Freigabe deaktiviert.
- `PlexUiLocaleId`: 1, muss auf einen aktivierten Locale-Eintrag zeigen.
- `PlexServerUri`: ausdrücklich konfigurierte HTTPS-Basisadresse des verbundenen Servers.
- `PlexServerIdentifier`: dessen tatsächliche machineIdentifier.
- `PlexRegistrationLibraryKeys`: erlaubte Library-Section-IDs, z. B. `["7"]`.

Plex-Neuregistrierung braucht eine bestätigte E-Mail und Zugriff auf mindestens eine
dieser Libraries mit dem **Token des anmeldenden Plex-Nutzers**. Server-Identität wird
abgeglichen; URLs aus Plex-Ressourcen werden nicht als Ziel übernommen.
Verknüpfungen werden ausschließlich anhand Provider + bestätigter externer ID gefunden,
niemals anhand einer gleichen E-Mail. Registrierung erstellt keinen ersten Owner.

## Prüfung und offene Integration

`dotnet build src/Jularr.Backend/Jularr.Backend.csproj` baut nur die Bibliothek.
Betroffene Tests liegen unter `tests/Jularr.Backend.Tests`; PostgreSQL-Tests verlangen
`JULARR_TEST_DB` auf `127.0.0.1:15433`, Benutzer `jularr_test`, Administrationsdatenbank
`jularr_test_admin`. Sie erstellen und entfernen nur eigene zufällig benannte Datenbanken.

Noch offen: Frontend-Host-/Formular-Anbindung, explizite Owner-Ersteinrichtung,
Freigabe-/E-Mail-Verifizierungs-Oberfläche, 2FA/WebAuthn-Anmeldeablauf, Logout/Rotation,
Profile-Auswahl mit PIN und alte Android-/TV-Verbraucher. Accounts mit bestätigter
2FA werden derzeit ohne Anmeldung abgelehnt, nicht am zweiten Faktor vorbeigeführt.
Plex wurde mit kontrollierten Provider-Antworten getestet, noch nicht gegen eine echte Installation.
Weitere Profile-Funktionen und Phase B sind nicht Teil dieses Schnitts.
