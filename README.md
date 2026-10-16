# Jularr clean start

Ein ASP.NET-Host: `Jularr.Frontend` startet Razor Pages und API.
`Jularr.Backend` ist eine referenzierte .NET-Klassenbibliothek mit Services, Logic,
Data und PostgreSQL-Zugriff. Kein alter Web-Bereich, kein EF.

`Jularr.Integrations` bündelt externe Kommunikation als Klassenbibliothek.
`Logic/Plex` enthält den Plex-Client, `Data/Plex` seine Protokollantworten
und `Data/Common` gemeinsame Integrationsfehler. Backend-Services
entscheiden weiterhin über Anmeldung, Bibliotheksfreigabe und Registrierung.
Die bestehenden Host-Registrierungen bleiben unverändert. NAS ist noch nicht
implementiert; entschieden sind SMB, NFS und Synology API sowie per Docker Compose
freigegebene Pfade. Konfiguration erfolgt über Services und Frontend.

Regeln: [AGENTS.md](AGENTS.md), [CONVENTIONS.md](docs/CONVENTIONS.md).
Navigation: [CODEMAP.md](docs/CODEMAP.md).
Nächste fachliche Schritte: [Backend-Slices](docs/BACKEND_NEXT.md) (Plan, keine Implementierung).
Instanz-Einrichtung: [Wizard-Ablauf und Owner](docs/INSTANCE_SETUP.md) (Vorschlag).
Lokale Vorschau auf D: [Testserver und Aktualisierung](docs/LOCAL_PREVIEW.md).

## Docker Compose

[compose.yaml](compose.yaml) braucht nur Image, Port und Volumes; `.env.example` nach `.env`
kopieren, Release und Datenbankpasswort setzen, `docker compose up -d`, dann `http://<host>:8097`
öffnen und den Owner anlegen. Jede Funktion arbeitet ohne weitere Einstellungen: Das
[Image](Dockerfile) bringt Jellyfins FFmpeg (Hardware-Transcoding für Intel, AMD und NVIDIA), das statische fpcalc von Chromaprint, 7-Zip, par2 und den PostgreSQL-18-Client mit
(auf dem `PATH` gefunden; ein gesetzter Pfad gewinnt, ein leerer schaltet das Programm ab), aller
Zustand liegt unter `DataPath` (`/data` im Image: `keys`, `backups`, `trickplay`, `translation`, `ocr`),
Bibliotheken unter `/media` (`Storage:LocalPaths`). Eine leere Datenbank erhält beim ersten Start
alle frischen Baselines in einer Transaktion; eine bestehende wird nie verändert oder migriert.

## Backend im gemeinsamen Host

Das Frontend braucht ProjectReferences auf `../Jularr.Backend/Jularr.Backend.csproj`
und `../Jularr.Workers/Jularr.Workers.csproj`.
Die Host-Anbindung ist in dessen `Program.cs` eingebaut.
Razor verwendet Services und deren Datenverträge; der Host
verwendet die vorgesehenen `Service/Core`-Adapter für Registrierung und Authentifizierung.
Datenbankkonfiguration, Connection und Transaction gehören vollständig ins Backend.

```csharp
using Jularr.Backend.Service.Core;
using Jularr.Workers;

builder.Services.AddJularrServices(builder.Configuration);
builder.Services.AddJularrWorkers();
```

`Jularr.Workers` enthält nur die Hintergrund-Worker (Scans, Download-Überwachung,
automatische Suche, Plex-Sync); sie rufen ausschließlich versionierte System-Services auf.

Das Backend liest `JULARR_DB` und bindet den Abschnitt `Authentication` selbst.
Die DI-Registrierung erstellt eine gemeinsame Datenquelle bei Bedarf und entsorgt
sie mit dem Host. SQL-Verbindungen werden erst beim notwendigen Datenzugriff geöffnet.
Services teilen innerhalb eines Aufrufs eine Connection und Transaction.
Logic, SQL, Runtime, Auth-Prüfer, Cache und Plex-Client sind intern; das Frontend
erhält die Services über DI. Die CLR-Feldmetadaten bleiben für Formulare verfügbar,
PostgreSQL-Typmetadaten bleiben im Backend.

Nach `Build()`: `app.UseExceptionHandler()` für den registrierten zentralen Handler,
`app.UseRouting()`, `app.UseRateLimiter()`, `app.UseAuthentication()` und
`app.UseAuthorization()`; dann `app.MapJularrServices()` neben `app.MapRazorPages()`.
Keine zweite Cookie-Registrierung. Backend ergänzt die vorhandene Cookie-Konfiguration
um DB-validierende Events und Secure/HttpOnly/SameSite. Sessions und persistente Cookies
gelten standardmäßig 30 Tage für alle Geräte. Bei Nutzung nach halber Laufzeit verlängert
das Backend zuerst die gültige DB-Session transaktional und danach das Cookie; normale
Seitenaufrufe schreiben nichts. Widerruf, Logout und abgelaufene Sessions bleiben endgültig.
Die unabhängige Cookie-Sliding-Expiration ist deaktiviert, damit Cookie und DB zusammen verlängert werden.

Data Protection verwendet den stabilen Anwendungskontext `Jularr`, unabhängig vom
Release-Verzeichnis. Die Schlüssel liegen in `DataPath/keys` (im Image `/data/keys`,
sonst im lokalen Anwendungsdatenordner), abweichend in `DataProtection:KeyDirectory`;
unter Windows schützt sie zusätzlich DPAPI. Unter Linux den Schlüsselordner
durch restriktive Dateirechte und gegebenenfalls Volume-Verschlüsselung schützen; das
Verzeichnis enthält vertrauliches Schlüsselmaterial. Schlüssel und DB müssen Updates überleben.
Cookies und Provider-Secrets aus dem bisherigen pfadabhängigen Schutzkontext müssen beim
einmaligen Wechsel neu ausgestellt beziehungsweise neu hinterlegt werden.
Keine Migration; nur eine leere Datenbank erhält beim Start die [Baselines](database/README.md).

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
| `GET /api/v1/profiles` | Zugeordnete Profile seitenweise für die Auswahl lesen |
| `PUT /api/v1/auth/profile/{profileId:long}` | Ungepinntes Profile auswählen; Session-ID und Secret rotieren |
| `GET /api/v1/profiles/{profileId:long}` | Aktives, zugeordnetes Profile lesen |
| `PUT /api/v1/profiles/{profileId:long}` | DisplayName/UiLocaleId über den kanonischen Writer ändern |
| `GET /api/v1/sessions` | Eigene gültige Sessions seitenweise lesen |
| `DELETE /api/v1/sessions/{sessionPublicId:guid}` | Eigene Session widerrufen |
| `DELETE /api/v1/auth/session` | Aktuelle Session widerrufen und Browser abmelden |

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

Razor bindet Login, Plex, Profile-Auswahl, Session-Verwaltung, Logout und Profile-
Einstellungen direkt an diese Services. `ServiceError` klassifiziert erwartete
Fehler gemeinsam für API und Razor; `JularrPage` zeigt sie ohne Catch pro Seite.
Die verifizierte Profile-Sprache hat Vorrang vor Browser-Sprachhinweisen.
Admin-Routen verwenden Backend-Policies mit aktuellen DB-Rollenrechten.
MediaManager darf vorerst Medien manuell verwalten sowie Requests/Wanted bearbeiten.
Instanz-, Account-, Storage-, Provider- und Regelverwaltung bleiben Owner-only.
Rollenänderungen wirken auch mit bestehenden Cookies; Writes prüfen die Rolle erneut
unter derselben Session-Transaktion. Owner verwalten Accounts unter `/Admin/Accounts`
und UI-Beschreibungen/Übersetzungen unter `/Admin/Localization`; Settings verlinkt
die jeweils freigegebenen Bereiche. Medieninventar bleibt offen.

`/Admin/Instance` bietet Ownern Instanzname und Sprache über
`ReadInstanceSettingsV1` / `UpdateInstanceSettingsV1` mit Versionsprüfung.
Die API liegt unter `GET|PUT /api/v1/admin/instance/settings`; SQL und
Sprachvalidierung bleiben im Backend. Eine neue Installation benötigt hierfür
die frische [Instance-Baseline](database/Instance.sql) nach der Account-Baseline.
Library, Requests und Playback sind schaltbar: Abschalten sperrt die jeweiligen Services
und entfernt ihre Links, ohne Daten zu löschen.
User `ReadInstanceCapabilitiesV1` liefert die verfügbaren Funktionen; Public
`ReadInstancePresentationV1` liefert ausschließlich Name und Sprachdefault.
Shell/Login verwenden den Namen, bei eigener Benennung mit „by Jularr“.
Die aktive Profilsprache hat Vorrang vor dem Instanzdefault; exakte Sprache,
Elternsprache und Englisch werden vom gemeinsamen Backend-Katalog aufgelöst.
Weitere Module, Profilsichtbarkeit und der weitere Setup Wizard bleiben offen.

`/Admin/Works` legt kanonische Works manuell an; `/Admin/Works/{id}` bearbeitet
den Titel mit Versionsprüfung und erhält weitere Titelquellen. `/Details/{id}`
und `/Watchlist` lesen persistierte Medien und bearbeiten ausschließlich die
Merkliste des aktiven Profils. `/Admin/Requests/Users` liest das vorhandene
Kontenverzeichnis; Quoten/Regelzuweisungen sind noch nicht implementiert.
Diese Grundlage benötigt zusätzlich die frische [Media-Baseline](database/Media.sql).

`/Details/{id}` legt sprachbezogene Anfragen an; `/Requests` zeigt ausschließlich
Anfragen des aktiven Profils. `/Admin/Requests` bietet die Status-Tabs und
Freigabe/Ablehnung durch Owner/MediaManager. Freigabe verknüpft atomar einen
gemeinsamen Work-Target; `/Admin/Wanted` zeigt die gewünschten Sprachen und
pausiert/aktiviert diesen Bedarf. Wiederholte aktive Anfragen erzeugen keine
Dubletten. Paging bleibt eine begrenzte SELECT-Abfrage ohne Counts/HasMore.
Eine frische Installation benötigt dafür [Acquisition.sql](database/Acquisition.sql)
nach Account und Media. Regeln/Quoten, einzelne Episoden/Editionen, Stornierung,
automatische Suche, Downloads und Import sind noch nicht verbunden.

Owner konfigurieren SMB/NFS unter `/Admin/Storage` und `/Setup/Storage` über
dieselben Services. Passwörter werden geschützt gespeichert; normale Seiten
kontaktieren das NAS nicht. Verbinden, Trennen und Mountstatus sind ausdrückliche
Aktionen über den separaten Linux-Mount-Helfer. Lokale Compose-Pfade sind nur aus
`Storage:LocalPaths` wählbar; Wake-on-LAN ist pro Verbindung als „nur Owner“ oder
„automatisch bei Abruf“ einstellbar. Library-Pfade liegen auf derselben Seite unter
der Verbindung und werden über einen sicheren Ordner-Browser gewählt. Scans laufen
im Hintergrund-Worker, automatisch nach dem in `/Admin/Instance` eingestellten Intervall (Standard 24 Stunden, 0 aus; schlafende NAS bleiben unberührt), `/Admin/Library` zeigt Inventar und Zuordnung, `/Admin/Activity` die Scans, die
Detailseite die gespeicherte Verfügbarkeit. Dafür sind die frischen
[Storage-](database/Storage.sql) und [Library-Baselines](database/Library.sql)
erforderlich; ffprobe analysiert die Streams, fpcalc mit einem AcoustID-Schlüssel erkennt Musik per Fingerabdruck. Deployment, Rechte und
offene Integration stehen in [Storage Runtime](docs/STORAGE_RUNTIME.md).
Den tatsächlichen Umfang gegenüber dem Originalmodell zeigt der
[Schema-Abgleich](docs/SCHEMA_SNAPSHOT_ALIGNMENT.md).

Medienmodule/Profilsichtbarkeit, Metadatensprachregeln, Dateien/Cover, Monitoring,
Arr und Player sind damit noch nicht fertig. Bestehende Datenbanken werden nicht
automatisch erweitert; die lokale Vorschau bleibt separat.

`Service.Admin.Account.V1` bietet `ReadAccountsV1`, `ReadAccountV1` und
`UpdateAccountV1`: Name, Rolle und Aktivierung, mit Last-write-wins und Schutz des
letzten aktiven Owners. Rollen-/Statusänderungen widerrufen alte Sessions; Freigabe
verifiziert keine E-Mail. Parallele Account-Mutationen laufen serialisierbar und
melden einen bereinigten `concurrent_change`-Konflikt statt Daten zu überschreiben.
API: `GET /api/v1/admin/accounts`, `GET|PUT /api/v1/admin/accounts/{accountId:long}`.
`ReadAccountProfilesV1`, `AddAccountProfileV1` und `RemoveAccountProfileV1` verwalten
vorhandene Profile-Zuordnungen: `GET .../{accountId}/profiles`,
`POST|DELETE .../{accountId}/profiles/{profileId}`. Die Eigentümer-Zuordnung bleibt
erhalten; Entfernen widerruft betroffene Sessions atomar, ohne das Profile zu löschen.
`ReadAccountSessionsV1` und `RevokeAccountSessionV1` verwenden die bestehende Session-
Logic für `GET .../{accountId}/sessions` und `DELETE .../{accountId}/sessions/{sessionPublicId}`.
Auch der Owner erhält nur öffentliche Session-UUIDs, niemals Secrets oder interne IDs.

Listen liefern nur `Items`, `Page` und `PageSize`. Eine volle Seite bietet Weiter;
die folgende Seite darf leer sein. Keine zusätzlichen Paging- oder Zählabfragen.
Admin-Mutationen laden Listen nur beim Fehler erneut; Formulare und Buttons besitzen
gemeinsame Komponenten und Styles, keine nachgebauten Controls je Seite.

`/Admin/Providers` verwendet `ReadProvidersV1` und `UpdateProviderV1` zum Aktivieren
vorhandener Provider. API: `GET /api/v1/admin/providers`,
`PUT /api/v1/admin/providers/{providerId}`. Deaktiviertes Plex verschwindet aus dem
Anmeldeangebot.

## Plex-Anmeldung

Plex-Anmeldung und -Registrierung stellt der Owner unter `/Admin/Providers` beim verbundenen
Plex-Server ein (`ReadPlexSignInV1`, `UpdatePlexSignInV1`; standardmäßig aus): Anmeldung,
Registrierung, Freigabe neuer Konten (standardmäßig an) und die Bibliotheken, deren Nutzer sich
registrieren dürfen (keine gewählt: jede). Neue Plex-Konten starten in der Instanzsprache.
`Authentication:SessionLifetimeHours` bleibt 720 (30 Tage), zulässig 1–720.

Plex-Neuregistrierung braucht eine bestätigte E-Mail und Zugriff auf eine dieser Libraries
des verbundenen Servers mit dem **Token des anmeldenden Plex-Nutzers**. Server-Identität wird
abgeglichen; URLs aus Plex-Ressourcen werden nicht als Ziel übernommen.
Verknüpfungen werden ausschließlich anhand Provider + bestätigter externer ID gefunden,
niemals anhand einer gleichen E-Mail. Registrierung erstellt keinen ersten Owner.

## Gemeinsamer PostgreSQL-Textkatalog

Nach `AddJularrServices(...)` registriert der Host einmal alle englischen Frontend-Texte.
Referenz: [Frontend-Skeletons #960](https://github.com/Juloc/Jularr/pull/960).
`englishTexts` unten ist die vereinigte Schlüssel/Englisch-Liste aus dessen
ConsumerSources, AdminSources und englischen Shell-/Login-Texten, kein zusätzlicher Store.

```csharp
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core.Localization;

builder.Services.AddJularrLocalization(
    englishTexts.Select(entry => new UiTextResource(entry.Key, entry.Value)));
```

Backend-Fehlerressourcen werden automatisch ergänzt. Widersprüchliches Englisch für
denselben Schlüssel wird abgelehnt. Beim Hoststart werden Ressourcen idempotent
synchronisiert, nie das Schema installiert/migriert. Neue Texte brauchen keine SQL-Importe;
entfernte Deklarationen werden nicht gelöscht. Das frische Schema muss ausdrücklich
installiert sein; bestehende aktive Datenbanken werden während des Clean Cut nicht verändert.

Razor injiziert `ReadUiTextsV1` aus `Jularr.Backend.Service.Public.Localization.V1`,
lädt einmal pro Request und verwendet danach synchrone String-Lookups:

```csharp
using Jularr.Backend.Data.Public.Localization.V1;

var texts = await readUiTexts.ExecuteAsync<ReadUiTextsResultV1>(
    new ReadUiTextsParametersV1(profile.UiLocaleId), cancellationToken);
var heading = texts["discover.title"];
```

`ReadUiTextsResultV1` enthält `UiLocaleId`, `Locale`,
`IsRightToLeft`, die opake `Version` und `Texts`. Fehlende Schlüssel liefern ihren
Schlüssel; die normalen Strings müssen wie sonst durch Razor HTML-kodiert werden.
Der Host wählt die anonyme Kultur, z. B. anhand Accept-Language. `ReadUiLocalesV1`
liefert die aktivierten Sprachkennungen und IDs für diese Auswahl und die Sprachliste:
`await readUiLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: cancellationToken)`.
Auch anonyme Textaufrufe verwenden die gewählte ID; für Englisch dessen Katalog-ID,
keine hart codierte `1`. Profile bleiben ID-basiert.
Das Request-Bundle wird auch im Layout wiederverwendet; keine HTTP-/JS-Sprachanfragen.

Fallback je Schlüssel: exakte aktivierte Sprache → aktivierte Elternsprache →
englischer DB-Originaltext. Generated/Reviewed brauchen aktuelle MessageVersion;
Outdated/Missing werden übersprungen. Manual bleibt auch bei verändertem Original
verwendbar und wird nie automatisch überschrieben. Fehlende Übersetzungszeilen sind Missing.
Der sprachweise Cache ist auf ungefähr 4 MiB begrenzt und enthält keine Beschreibungen.
Sprachpakete und Sprachliste werden höchstens 30 Sekunden wiederverwendet und dann
beim nächsten Bedarf neu geladen. Warm Requests öffnen keine DB-Verbindung, laufen
aber durch denselben Public-Gate und dieselbe Feldvalidierung. Die gemeinsame
Read-Transaction wird erst beim ersten SQL-Aufruf geöffnet; Writes bleiben unverändert.
Nach Änderungen gilt diese maximale Cache-Verzögerung.

Die statische SELECT-Abfrage steht direkt im Public-Service auf `sql.Statement`.
PostgreSQL bestimmt die BCP-47-Elterncodes durch sukzessives Entfernen des letzten
Tag-Segments, priorisiert gültige Übersetzungen mit `DISTINCT ON` und verwendet
`COALESCE` für den englischen Originaltext. `jsonb_object_agg` liefert das komplette
aufgelöste Paket in einer Abfrage; keine C#-Fallback-Schleifen oder inneren Connections.
Externe Clients nutzen dieselben Services über `GET /api/v1/localization/locales`
und `GET /api/v1/localization/texts/{uiLocaleId:long}`. Razor macht dafür keinen HTTP-Aufruf.

KI-Kontext liegt nur in `UiTranslationMessages.Description`. Neue Schlüssel starten
ohne Beschreibung; die Synchronisierung erfindet und überschreibt sie nicht.
`Service.Admin.Localization.V1` bietet `ReadUiMessagesV1`, `ReadUiMessageV1`,
`UpdateUiDescriptionV1` und `SaveUiTranslationV1` über den kanonischen Logic-Writer.
`/Admin/Localization` zeigt paginierte Originaltexte, gespeicherte Übersetzungen,
Status und Quellversion. Beschreibungen und manuelle Übersetzungen sind editierbar;
Quell- und Übersetzungsversionen verhindern veraltetes Überschreiben. Benannte
Platzhalter wie `{name}` werden geprüft. `TranslationVersion = null` bedeutet eine
neue Übersetzung, nicht bedingungsloses Überschreiben. Nur aktivierte Sprachen sind
beschreibbar. Beschreibungen gehören zur globalen Quelle, nicht zu einer Sprache:
`PUT /api/v1/admin/localization/messages/{uiTranslationMessageId}/description`.
`/Admin/Languages` nutzt die Admin-Services `ReadUiLocalesV1`, `CreateUiLocaleV1`
und `UpdateUiLocaleV1`: Sprachkennung, Anzeigename, Richtung und Aktivierung.
API: `GET|POST /api/v1/admin/localization/locales`,
`PUT /api/v1/admin/localization/locales/{uiLocaleId}`. Die kanonische Field-Validierung
akzeptiert vom Server unterstützte .NET-Sprachkennungen und normalisiert deren
Schreibweise. Versionsprüfung schützt Änderungen; Englisch bleibt aktiviert.
Das Abschalten der Profile-Sprache erhält weiterhin den Elternsprach-Fallback.
KI-Anforderung ist noch nicht implementiert.
Normale Seitenaufrufe und Hoststart führen keine KI-Übersetzung aus.

ProblemDetails enthält `code`, `resourceKey`, `traceId` und bei Feldfehlern `errors`
(Codes) plus `errorResourceKeys`. Der gemeinsame Katalog enthält `errors.*`; es gibt
keine lokalisierte Exception oder zweite Fehlerübersetzung. Für direkte Razor-Aufrufe
liefert `ServiceErrorResources.GetKey(code)` denselben Schlüssel.

Offline-Vorbereitung: `Texts` ist ein vollständiges aufgelöstes Sprachpaket, nicht nur
eine Seite. `Version` berücksichtigt alle Fallback-Schichten. Ein späterer Offline-Sync
kann pro lokalem Profile komplette Pakete speichern und gleiche Sprachen deduplizieren.
Export, Offline-Speicher und Synchronisierung sind noch nicht implementiert.

Das Frontend registriert seine englischen Deklarationen über `UiResources.English`.
`UiText` lädt den gemeinsamen Katalog über die Public-Services und teilt das Paket
zwischen Seite und Layout. Es gibt keinen separaten Frontend-Übersetzungsspeicher.

## Prüfung und offene Integration

`dotnet build src/Jularr.Backend/Jularr.Backend.csproj` baut nur die Bibliothek.
Betroffene Tests liegen unter `tests/Jularr.Backend.Tests`; PostgreSQL-Tests verlangen
`JULARR_TEST_DB` auf `127.0.0.1:15433`, Benutzer `jularr_test`, Administrationsdatenbank
`jularr_test_admin`. Sie erstellen und entfernen nur eigene zufällig benannte Datenbanken.

Frische Instanzen öffnen über die Anmeldung `/Setup`. Dort erstellt der Benutzer
den ersten Owner ohne Einrichtungscode; Account, Passwort, eigenes Profile,
AccountProfiles-Zuordnung und Anmeldung werden gemeinsam gespeichert. Eine dauerhafte
Setup-Markierung verhindert weitere öffentliche Owner-Erstellungen, auch bei parallelen
Anfragen. Passwortregeln und Bestätigung kommen aus dem Backend-Datenvertrag.
Voraussetzung bleiben die explizite Datenbankkonfiguration und die installierte
frische Baseline; der Host installiert kein Schema. Nach diesem ersten Schritt öffnet
sich die Storage-Verbindungsseite. Weitere Wizard-Schritte und die abschließende
Setup-Freigabe sind noch nicht verbunden.

Noch offen: weitere Setup-Konfiguration und Systemprüfungen, fachliche Medien-Admin-Operationen,
E-Mail-Verifizierungs-Oberfläche, 2FA/WebAuthn-Anmeldeablauf,
Profile-Auswahl mit PIN, vollständige Profile-Verwaltung und alte Android-/TV-Verbraucher. Accounts mit bestätigter
2FA werden derzeit ohne Anmeldung abgelehnt, nicht am zweiten Faktor vorbeigeführt.
Plex wurde mit kontrollierten Provider-Antworten getestet, noch nicht gegen eine echte Installation.
Details/Watchlist, manuelle Medienverwaltung und Requests/Wanted verwenden echte
Services. Der Player spielt kompatible Dateien direkt (Direct Play mit HTTP-Range),
setzt pro Profil exakt fort und markiert nur ein echtes Ende als gesehen. Andere
Dateien laufen als HLS: FFmpeg kopiert passende Spuren (Remux) oder wandelt nach
H.264/AAC (Transcode), erzeugt Segmente erst bei Bedarf und begrenzt Vorlauf und
parallele Jobs. Konfiguration: `Media:TranscodeCachePath`, `Media:MaxTranscodes`, optional
`Media:VideoEncoderArguments` und `Media:HardwareAccelerationArguments` mit Software-Fallback.
Vorschaubilder beim Spulen (Modul „Trickplay“, standardmäßig aus) rendert ein Hintergrund-Worker
mit FFmpeg nach `DataPath/trickplay`.
Metadaten-Spiegel ersetzen die Adressen von TMDB, MusicBrainz, AniList und Open Library über
`Providers:Tmdb`, `Providers:MusicBrainz`, `Providers:AniList` und `Providers:OpenLibrary` (HTTPS, auf dem eigenen Rechner auch HTTP).
Filme nennen ihre TMDB-Filmreihe (Modul „Franchises“, eigene Seite `/Franchise/{id}`). Jede Datei
behält die Release-Angaben ihres Namens (Gruppe, Qualität, Edition, Proper); Admin → Bibliothek → Benennung
legt die Namen von Film- und Folgendateien mit Sonarr-artigen Platzhaltern fest. Ein selbstsigniertes
DSM-Zertifikat vertraut Jularr erst, nachdem der Owner seinen Fingerabdruck auf der Speicher-Seite bestätigt hat.
Fehlende Untertitel der gewünschten Sprachen (Modul „Untertitel“, standardmäßig aus) holt Jularr von Anbietern, die wie
Indexer verwaltet werden (OpenSubtitles.com, SubDL), und legt sie als `<Video>.<Sprache>.srt` neben das Video.
Ihre Verträge stehen in [BACKEND_NEXT](docs/BACKEND_NEXT.md).
Phase B bleibt gestoppt. Razor-Integrationstests verwenden nur isoliertes PostgreSQL
und verlangen vorher `npm run build` im Frontend für die echten Asset-Manifeste.

Arr+ betreibt Jularr vollständig selbst. Nativer Downloader und optionale externe
Downloader wie SABnzbd verwenden dieselbe Pipeline. Der native Downloader nutzt
News-Server (`nntp://` oder `nntps://`, Login, Verbindungen) unter `/Admin/Acquisition`;
Reparatur und Entpacken übernehmen par2 und 7-Zip aus dem Image. Ohne diese Programme warten
beschädigte oder gepackte Downloads mit Hinweis. Sonarr/Radarr/Prowlarr dienen
nur einer ausdrücklich gewählten einmaligen Übernahme (`/Admin/Acquisition/Takeover`:
Vorschau, dann Übernahme von Indexern, SABnzbd und fehlenden Titeln), nicht dem laufenden Betrieb.
Der Player wird von Jularr selbst gehostet; vorhandene Dev-Technik ist Referenz.

Backups (`/Admin/Backup`) sichern die Datenbank mit `pg_dump` samt
Schlüsselbund in `DataPath/backups` (abweichend `Backup:Path`); automatisch alle
`Backup:IntervalHours` (Standard 24, 0 aus), die neuesten `Backup:Keep` (Standard 7, mindestens 1) bleiben.
Ein Backup derselben Jularr-Version stellt `pg_restore` nur in der
Einrichtung wieder her (`/Setup/Backup`); danach Jularr neu starten. Mit `Backup:Passphrase` liegt der
Schlüsselbund nur verschlüsselt im Backup (AES-GCM, Schlüssel per PBKDF2) und die Wiederherstellung fragt
nach der Passphrase; ohne sie enthält der Backup-Ordner die Schlüssel für gespeicherte Zugangsdaten offen
und muss geschützt liegen.
