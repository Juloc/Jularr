# Profile: eine Read- und eine Update-Funktion

Review-Stand auf dem Framework aus #943. Nach diesen beiden Funktionen stoppen;
kein vollständiger Profile-Umbau, kein Merge nach dev, kein Datenbank-Cutover.

## Implementierter Ablauf

- `GetProfile`: `UserReadService<ProfileParameters>`, statischer SELECT in einer
  echten PostgreSQL-READ-ONLY-Transaktion. Nur das aktive, zugängliche Profil.
- `RenameProfile`: `UserService<ProfileParameters, RenameProfileData>`, Operation
  `Update`. Logic prüft/trimmt den Namen und führt das UPDATE aus. Session-,
  Account-, Membership- und Profile-Locks sowie UPDATE und Ergebnis-SELECT
  verwenden dieselbe Service-Connection und -Transaktion. Fehler rollen zurück.
- Die beiden HTTP-Adapter sind `GET /api/v1/profiles/{publicId}` und
  `PUT /api/v1/profiles/{publicId}/name`. Cookie-Schreibzugriffe benötigen den
  Antiforgery-Cookie und `RequestVerificationToken` aus der GET-Antwort.

`profile.self.read` und `profile.self.rename` sind keine frei vergebenen Grants:
`ProfileServiceGate` erkennt exakt diese beiden Operationen. Lesen verlangt eine
aktuelle Session und Account/Profile-Mitgliedschaft. Umbenennen verlangt zusätzlich
`Profiles.OwnerAccountId = ActorAccountId`. Mitgliedschaft allein ist kein
Verwaltungsrecht. Unbekannte Permissions, Ressourcen, Module und Service-Areas
werden abgelehnt. Diese User-Funktion führt keinen Admin-Bypass ein.

## Data und Felder

Die öffentlichen Parameter, Eingaben und Ergebnisse stehen zusammen unter
`Data.User.Profile.V1`. `ProfileFields` definiert einmal pro Feld Tabelle,
Spalte, CLR- und PostgreSQL-Typ. Dieselbe Definition wird in öffentlichen und
neutralen Data-Klassen verwendet. Der bestehende `SqlParams`-Binder verarbeitet
nur im statischen SQL tatsächlich verwendete Eigenschaften; kein SQL-Generator
und kein automatisches Binden aller Request-Felder.

```csharp
[property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
string DisplayName

[property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName), false)]
string? DisplayName
```

Der kurze Konstruktor bedeutet Pflichtfeld; die Überladung erlaubt optionale
Werte. SQL-NULL behält seinen konkreten PostgreSQL-Typ. Pflichtfelder verweigern
NULL beim Binden und beim Lesen. Optionalität ersetzt keine Geschäftsregel und
ist noch kein PATCH-Vertrag für „nicht geliefert“ versus „explizit null“.
JSON enthält weiterhin normale skalare Felder, keine Metadaten-Wrapper.

## Auth- und Integrationsgrenze

`AddProfileReferenceServices` registriert den konkreten Gate, Runtime und beide
Services; `MapProfileReferenceEndpoints` verbindet die beiden HTTP-Adapter.
Die Tests rufen diese echten Registrierungen auf, nicht einen AllowAll-Gate.

Der Gate akzeptiert ausschließlich eine authentifizierte Cookie-Identität mit
den signierten Claims `jularr.session_id` und `jularr.session_revision`. Die UUID bezeichnet einen Datensatz
in `AccountSessions`, **keinen Bearer-Token**. Ablauf, Widerruf, CredentialRevision, RotationRevision,
Account-Aktivierung und aktives Profile werden in PostgreSQL geprüft. Der
Read-SELECT prüft den Sessionzustand nochmals; der Logic-Gate sperrt die Zeilen
innerhalb der Schreibtransaktion. Interne IDs und Joins bleiben bigint/long.

Noch **nicht** umgestellt: `Program.cs`, Login/Session-Ausstellung und
`OwnerAuthService`/`CurrentAccountContext`; bestehende Profile-Razor-Seiten,
Profil-Einstellungen, Devices/Notifications sowie Android/TV-Client-Routen.
Alte String-ID-Cookies werden nicht umgedeutet. Die neue Registrierung und die
neuen Routen sind deshalb noch nicht in der laufenden Anwendung aktiviert.
Es gibt keine Dual-Writes und keine Live-Migration.

## Nachweis

`ProfileServiceGateTests`: echte DI, geschütztes Cookie, HTTP-Read/Update und
Antiforgery gegen isoliertes PostgreSQL; Session-Widerruf/Ablauf, Credential-Wechsel,
deaktivierter Account, fehlendes aktives Profil, Membership ohne Eigentum,
unbekannte Berechtigungen und Module. `ProfileReferenceVerticalTests`: Fremdprofil,
Gate-Reihenfolge, atomare Mutation und Rollback. `DataFieldTests`: gemeinsame
Felddefinition, Required/Optional, echte PostgreSQL-NULL- und Parameterabbildung.
Framework-READ-ONLY und Transaktionsverhalten sind zusätzlich in den bestehenden
`SqlContextTests`/`ServiceFoundationTests` abgesichert.

Das Testschema enthält die für diese Operationen benötigten Zielspalten und
Constraints, nicht die vollständige neue Datenbank-Baseline. Grüne Tests bedeuten
keinen abgeschlossenen Cutover und kein vollständig implementiertes Framework
für alle zukünftigen Admin-/System-/Worker-Funktionen.
