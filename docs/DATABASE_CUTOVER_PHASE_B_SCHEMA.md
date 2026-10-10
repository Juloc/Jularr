# Phase B — neue PostgreSQL-Zielstruktur (erster konkreter DDL-Entwurf)

**Stand: IN ARBEIT / NICHT FREIGEGEBEN (10.10.2026).** Keine produktive Datenbank, Migration oder laufende `dev`-Installation wurde verändert. Dieser Entwurf verwendet ausschließlich eine **neue, leere** PostgreSQL-Datenbank als spätere Testbasis. Weder alte IDs noch alte Spalten oder die 56 historischen Migrationen werden übernommen.

## Quellen und Umsetzung

- Fachlich maßgeblich: [CLEAN_CUT_DATABASE.md](CLEAN_CUT_DATABASE.md) §2, §3 und §8.
- Ergebnis aus Phase A: 19 Fachverantwortungen in [PR #945](https://github.com/Juloc/Jularr/pull/945), alter Live-Katalog nur zur Funktionskontrolle (152 Anwendungstabellen/56 Migrationen), nicht als DDL-Schablone.
- Backend-Grenzen: [SERVICE_DATA_LOGIC_ARCHITECTURE.md](https://github.com/Juloc/Jularr/blob/docs/service-data-logic-contract-20261010/docs/SERVICE_DATA_LOGIC_ARCHITECTURE.md), #852/#942. Auth-/Profilrechte bleiben im Service-Gate; **nur Logic schreibt**. Service-Read hat echte PostgreSQL-READ-ONLY-Transaktion.
- Konkreter Entwurf: [01_CORE_DRAFT.sql](DATABASE_CUTOVER_PHASE_B_01_CORE_DRAFT.sql) und [02_PROGRESS_MEDIA_DRAFT.sql](DATABASE_CUTOVER_PHASE_B_02_PROGRESS_MEDIA_DRAFT.sql), in dieser Reihenfolge. **Beides sind DDL-DRAFTS, KEINE freigegebene ausführbare EF-Baseline.** Die Dateien enthalten bewusst vorläufige Felder und Semantik und dürfen nicht auf `dev` ausgeführt werden.
- Offene B-Entscheidungen und tatsächliche Abdeckungslücken: [DATABASE_CUTOVER_PHASE_B_DECISIONS.md](DATABASE_CUTOVER_PHASE_B_DECISIONS.md).

## Was jetzt physisch entworfen ist

**81 Tabellen als konkret benannte PostgreSQL-`CREATE TABLE`-Statements (45 Kern + 36 Fortschritt/Artwork/Jobs).** Zusammen bilden sie einen weitreichenden Target-Entwurf, nicht die gesamte bereits reviewte Zielstruktur.

| Fachbereich | Tatsächlich in Draft-SQL modelliert | Fehlende/noch zu bestätigende Fachanteile |
| --- | --- | --- |
| Accounts / Profile / Auth | Accounts, AccountRoleTypes, Profiles, AccountProfiles, AccountPasswords, AccountPasskeys, AccountTotpFactors, AccountRecoveryCodes, AccountExternalLogins, AccountSessions, AccountAuthChallenges, Providers, UiLocales | Auth-Challenge-Typen, WebAuthn-Details, externe Plex-PIN-Nonce-/Link-Vorgänge, optionale AccountPermissions/Groups, effektive Profileinschränkungen |
| Works / Titles / Metadata / Relations | Works, MediaTypes, WorkTitles, WorkExternalIdentities, WorkMetadataFacts, WorkLocalizedValues, WorkFieldProvenance, WorkFactTypes, WorkRelations/Types, Franchises/FranchiseWorks, WorkMediaClassifications/Types | EditionExternalIdentities, WorkCredits/Narrators, weitere tatsächlich nötige getypte Metadatenfelder, Provider-Namespace-Normalisierung/Transaktionen |
| Film / TV / Anime / Reader-Units / Music | WorkSeasons, WorkEpisodes, WorkVolumes, WorkChapters, WorkTracks, WorkEditions, WorkVersions | Musik-Artist/Recording-Spezialidentitäten, Cross-Media-Units und eindeutige externe Chapter-/Season-Fakten verifizieren |
| Media / Files / Games | MediaAssets/Types, LibraryRoots, StoredFiles, MediaTracks/Types, GamePlatforms, GameReleases, GameReleaseStoredFiles, GameReleaseFileRoleTypes, StoredFileHashes | MediaTechnicalAnalyses/Stream-Details, tatsächliche File-Attachments/Launcher-/Release-Hashes; Game-only FK-Absicherung |
| Personal State | MediaProgress, ProgressPositionTypes, Time/Reading/GameProgressPositions, WatchlistEntries, Collections, CollectionWorks, ProfileRatings, MediaPlaybackHistory | Offline-Event-Idempotency/Revision, Ratings-Skala und Progress-Client-Semantik |
| Player / Images / Reader | PlaybackSessions, Images, ImageTypes, ImageTargetKindTypes, ImageTypeTargets, ImageAssignments, MediaChapters, MediaSegments/Types/SourceTypes, MediaDetectionRuns/Types/StatusTypes, ReaderContent/Pages/Bookmarks/Highlights | Companion-/Pairing-Session-Lebensdauer, Image-Generation-Presets (bedingt), Reader-Translations, effektive MediaAsset-Version-Sicherheit |
| Background / Acquisition / Locale | Operations, OperationLogs, OperationStatusTypes, AcquisitionRequests/StatusTypes, Notifications, UiTranslationMessages, UiTranslations, ImageGenerationRequests | Wanted/Rules/Downloads/Import/Arr, Notification-Channels/Preferences/Delivery, AI/Learning/Curriculum und Provider-Grants/Sync |

**Ergänzende relationale Fachentscheidungen aus dem Entwurf (noch mit PostgreSQL-Betrieb zu testen):**

1. **Accounts vs Profiles:** `Accounts.Email` ist `citext UNIQUE NOT NULL`, `Profiles.OwnerAccountId` ist echte Login-Account-FK; `AccountProfiles(AccountId,ProfileId)` gibt Auswahlrecht, **nicht** Manage-Rechte. Ein zusätzlicher **DEFERRABLE zyklischer FK** verhindert nach COMMIT einen Profile-Owner ohne AccountProfile-Link. `AccountSessions( AccountId, ActiveProfileId)` hat zusammengesetzte FK auf AccountProfiles; widerrufene/übertragene Profile werden transaktional gehandhabt.
2. **One Work:** Ein `Works.Id bigint` ist Root für Movie/Series/Book/Manga/LightNovel/Music/Game; Anime ist Klassifikation, GameRelease verweist ausschließlich auf `WorkVersions.Id` ohne eigene GameId. `WorkEpisode/Chapter/Track/Edition` besitzen je eine `UNIQUE(Id,WorkId)` für echte Cross-Work-Membership-FKs. Franchises sind keine WorkRelations.
3. **Keine offene EAV-Datenhalde:** `WorkMetadataFacts` und `WorkLocalizedValues` haben explizit **getypte, benannte** Felder, keine `FactKey/Value JSONB`-Tabelle. `WorkFieldProvenance` nutzt eine code-geprüfte Typ-FK, nicht beliebige vom Client stammende Feldnamen. Verbleibende echte Facts in B typisiert ergänzen.
4. **Progress und echte Total-Subtype-Invariante:** `MediaProgress(ProfileId, WorkId, optional genau ein Unit-Target)` nutzt `UNIQUE NULLS NOT DISTINCT`; jede Unit-FK referenziert auch `WorkId`. Für `ProgressPositionTypeId=1/2/3` erzeugen generierte Spalten den passenden Detail-FK. **Zweiseitig DEFERRABLE FKs** erzwingen, dass beim Commit genau die entsprechende Time-/Reading-/Game-Position existiert und eine falsche Positionstabelle nicht referenziert werden darf. Konkrete Insert-/Update-/Delete- und Concurrency-Tests sind Pflicht; kein Trigger-basierter Geschäftsablauf.
5. **Image-Ziele:** `ImageAssignments` hat `num_nonnulls(WorkId, WorkChapterId, MediaChapterId)=1` plus FK-geprüfte Target-Art und erlaubte ImageType/TargetKind-Kombinationen über `ImageTypeTargets`. Exakte Kombinationen/Seedwerte vor finaler DDL entscheiden. Spiele bebildern `Works.Id`, nicht `Games.Id`.
6. **Zeitleisten:** Kapitel/Segments sind immer auf genauem `MediaAssetId`; `StartMs >=0 AND EndMs>StartMs`; wiederholte/überlappende Intro/Recap/Outro/Credits-Marker erlaubt. Detection-Run mit `MatchCount=0` bleibt erfolgreicher NoMatch und darf den Scanner vor unnötigen Reruns schützen.
7. **Operationen:** Ein gemeinsames Operations/Logs-Modell. `Execute`-Service/Direct/Queue/Force sind **separat autorisierte Abläufe**; kein langes SQL-Transaction-Window über NAS/Provider. Durables Claim/Retry/Outbox und Partner-Mutationen brauchen die endgültigen Anforderungen aus B.
8. **Indizes:** Nur belegbar nützliche Startindizes: Account-Mail, ProviderIdentity, Work/Title-Search, Work->Children, Root/RelativePath, Progress-Continue, Watchlist-Neu, Image/Locale, Timeline, Operations-Claim. PostgreSQL `pg_trgm` ist im Entwurf **tatsächlich verwendet**; `citext` für die Case-insensitive Email. Finaler Satz erst nach den statischen Service-SELECTs und `EXPLAIN (ANALYZE, BUFFERS)`.

## Noch KEIN Gate B

Die 81 Tabellen sind ein **prüfbarer erster physischer Entwurf**, keine Abnahme aller 19 Fachbereiche. Insbesondere fehlen abschließende Tabellen/Felder für Music-Artist/Recording, AI/Learning/Curriculum, Wanted/Rules/Arr, Notifications-Channels, externe Provider-Grants und Event-Idempotenz; `CLEAN_CUT_DATABASE.md` entscheidet diese Funktionsanforderungen, aber nicht sämtliche Spalten.

Zudem sind Seeds für die echten `enum : byte`-Kontrakte nicht bestätigt. Im SQL sind einige Zahlen ausdrücklich nur **PROPOSED**, mehrere Typ-FKs absichtlich **ohne Seed**. Für ein Release sind genau definierte, stabile numerische Seed-IDs mit entsprechenden .NET-Enums erforderlich.

**Phasenfolge:** fehlende Fachanteile + Entscheidungen schließen → SQL syntax/constraint checks in einer **isolierten wegwerfbaren PostgreSQL-Test-DB** und EF-Core-Design/ModelSnapshot → erst nach Gate B die **einzige** echte Baseline in Phase C erstellen. Das bestehende `dev` und die aktuell getestete Demo-DB werden nicht angefasst; echter Reset nur Phase G nach separater Freigabe.

## Erwartete B-Abnahme (noch offen)

- [x] Aktuelles Domänen-/Fachziel ist vom Legacy-Inventar getrennt und in eigenem Branch dokumentiert.
- [x] Ausgewählte kritische Tabellen bekommen konkrete Spalten, FK, CHECK/UNIQUE und Index-Entwürfe.
- [ ] **Alle** 19 Funktionsbereiche mit tatsächlich benötigten eigenen Tabellen/Feldern/Units berücksichtigt, ohne doppeltes EAV/DB-State-Root.
- [ ] Alle Type-Enum-Ids (C# byte ↔ PostgreSQL smallint), Defaults, nullable- und Delete-Regeln vom Besitzer bestätigt.
- [ ] Progress-Subtype-Zyklus, Owner-Profil-Link, GameRelease-WorkType, Media-Asset-Link, Image-Target-Typ, Offline-Revision/Sessions und Provider-Mutationen real auf PostgreSQL verifiziert.
- [ ] Auth/WebAuthn/TOTP/Plex/Jellyfin/Notification/Learning/Arr fachliche und Sicherheitslücken geschlossen.
- [ ] Neue wichtige statische Read-Queries mit realistischen Seeds ausgeführt, korrekte Pagination/WorkCards/N+1-Vermeidung, `EXPLAIN` für geplante Indizes geprüft.
- [ ] Keine offene produktentscheidende Frage, vollständiges referenzierbares Zieltable-/EF-/raw SQL-Inventar, und Freeze-/PR-Integrationsplan für Phase C.
