# Erste Framework-Referenzfunktion – Profilname (Review-Stop)

**Ziel:** Prüfen, ob das vorhandene Service/Data/Logic-Framework zum tatsächlichen
Umbau taugt. Nur diese kleine Funktion auf eigenem Branch; **keine** weitere
Schema-Phase, keine neue Service-Engine, kein Store-Layer.

## Konkreter Ablauf

1. Ein autorisierter Client verwendet ausschließlich `ProfileParameters.PublicId : Guid`.
   `Profiles.Id bigint` sowie `AccountProfiles.AccountId/ProfileId bigint` bleiben
   **interne** SQL-/Join-/Scope-Identitäten. UUID ist kein Passwort/Token.
2. `GetProfile : UserReadService<ProfileParameters>` delegiert einen einzigen
   statischen, parametrierten SELECT an die ReadSql-Fassade. Profil- und
   Account-Zugehörigkeit sind in demselben SQL-Prädikat geprüft; PostgreSQL
   `READ ONLY` kommt aus dem vorhandenen Framework.
3. `RenameProfile : UserService<ProfileParameters, RenameProfileData>`
   deklariert `ServiceOperationType.Update`. Nach dem zentralen
   Modul-/Caller-/Permission-Gate und dem transaktionalen Recheck ruft
   der Service genau eine neutrale `Jularr.Logic.Profile.ProfileLogic.RenameAsync`
   auf. **Nur dort liegt das UPDATE**; der Ergebnis-SELECT läuft noch in
   derselben Service-Transaktion/Connection.
4. Ein fremdes, inzwischen abgemeldetes, deaktiviertes oder nicht zum aktiven
   Profil gehörendes PublicId darf weder Daten lesen noch ändern. Der
   SQL-Writer nutzt `@ActorAccountId` und `@ActiveProfileId` aus
   dem vertrauenswürdigen `SqlContext`, niemals aus User-DTO-Feldern.
5. Alle neuen Ziel-Vertragsklassen liegen gemeinsam in
   `Data/User/Profile/V1/Profile.cs`; die Logic erhält ausschließlich
   einen rollenfreien neutralen `Data/Profile/ProfileNameChange`.

## Review-Dateien

- `src/Jularr.Web/Data/User/Profile/V1/Profile.cs`
- `src/Jularr.Web/Service/User/Profile/V1/ProfileServices.cs`
- `src/Jularr.Web/Logic/Profile/ProfileLogic.cs`
- `tests/Jularr.Tests/ProfileReferenceVerticalTests.cs`

Die Tests nutzen pro Lauf ein **isoliertes PostgreSQL-Schema** mit der für
diesen Anwendungsfall benötigten Teilmenge des Zielvertrags und einen
**streng prüfenden** Fixture-Gate. Sie decken Lesen/Umbenennen,
fremde UUID, mehrfach berechtigte Membership bei falschem aktivem Profil,
Revocation, disabled Account, transaktional verweigerte Mutation,
Rollback bei ungültiger Eingabe und sichere SQL-Parameter ab.

**Review-Grenze:** Der Gate im Test ist kein Production-AllowAll-Gate und
wird nicht als solcher registriert. Reale ASP.NET-DI, Auth-Session-Verifikation,
Pg-Baseline mit **allen** Zielconstraints und HTTP-/Android-/TV-Consumer
bleiben unverändert, bis dieses Muster fachlich und technisch geprüft ist.
Dieser Branch basiert auf dem noch nicht gemergten Framework-PR #943.
Ein grüner fokussierter Test beweist nur die Referenzfunktion und keine
Fertigstellung des gesamten Cutovers.

**Entscheid für das Review:** Ist diese klare Richtung
Data → Service-Read/Orchestrierung → neutraler Logic-Writer →
gemeinsamer SqlContext richtig? Nach Freigabe wird derselbe Ablauf
auf die nächste echte Funktion angewendet, nicht eine neue Abstraktion erfunden.
