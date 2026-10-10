# Phase B — offene Schemaentscheidungen und konkrete Nacharbeiten

**Status: OPEN.** Die zwölf PostgreSQL-DDL-Draftabschnitte (168 Target-Tabellen) sind keine freigegebene Baseline. Dieser Registereintrag verhindert, dass ein Agent unbemerkt fehlende Fachfunktionen durch schöne, aber noch nicht vollständig abgenommene Target-Tabellen ersetzt. Keine 1:1-Migration der Legacy-Tabellen.

**Gezielte Bereinigung (10.10.2026):** Die rein internen UUID-Roots
für AcquisitionIndexers, AcquisitionDownloadClients und
Curriculum*/SharedCourseInstances/LearnerCourses sind gemäß dem freigegebenen
`bigint`-Grundsatz auf Identity/`bigint` umgestellt; dazugehörige FKs,
Scratch-Tests und das Tabellenmanifest wurden angepasst. Die ursprüngliche
Ausnahmebegründung „war bereits UUID im Altcode“ allein reicht bei einem
Greenfield-Clean-Cut nicht aus. Echte ClientEvent-/Idempotenz-UUIDs bleiben.
`WorkTitles` hat nun eigene Titelprovenienz und manuelle Override-Metadaten;
`AccountPasskeys` führt AAGUID/Transports/Backup-/UV-Felder;
`Profiles.IsLearningEnabled` hält die ausdrücklich vereinbarte opt-in-Einstellung
(default Off). Noch fehlende Inhaltsrestriktionen, Auth-/Titelauflösungs-/Consent-
Semantik und vollständige Work-Facts sind **weiterhin B02/B04/B05-Blocker**.
Nicht erforderliche `AccountPermissions`/`ImageGenerationPresets` bleiben
bedingt, statt ohne nachgewiesene Nutzung erfunden zu werden.

| ID / Priorität | Entscheidung, **vor finaler DDL** | Vorhandene Quelle / Risikobereich | Bereits vorgeschlagen, NICHT beschlossen | Abnahme |
| --- | --- | --- | --- | --- |
| B01 / BLOCKER | **Enum-Codes und Seeds**: Für alle persistierten \`enum : byte\` echte feste Werte für MediaTypes, Roles, ProgressPositionTypes, Segment-Types/Source, ImageTypes/TargetKind, DetectionTypes/Statuses, OperationStatusTypes, MediaTrackTypes, MediaAssetTypes, FileRoles, Relations/Classification und RequestStatus abstimmen. | \`CLEAN_CUT_DATABASE.md §2\`, vorhandene C# Enums, #943 ServiceOperationType; alte Codes werden **nicht** als Target vorausgesetzt | Im DDL sind \`1/2/3\` teils als Vorschlag und einige Typentabellen unbesetzt markiert | Expliziter seed manifest + C# byte Enum genau gleich, Fremdschlüsseltest |
| B02 / BLOCKER | **Account/Profile/Auth-Lebenszyklen**: reale Passkey-Felder (AAGUID, Transport, UV flags), Recovery-/TOTP-Reset, Challenge-Zwecke, Sessionrotation und die Trennung Account/Plex PIN/externes Linken. | \`AccountExternalLogins\`, \`AccountSessions\`; PR #920 adds PlexLoginAttempts.Purpose | \`AccountProfiles\` mit Owner-FK und AccountSessions composite FK; PIN kein Account-Auth | Login/Link/Onboarding/Revoke/Transfer transaktional und CSRF/Rate limits; keine Legacy-ID |
| B03 / BLOCKER | **Progress-Unit und Position invariant**: Detailtyp genau 1, Cross-Work-Targets, Edition/Reader-Anchor-Revisions, Offline-Client-Idempotency-Event-Keys, Race/Conflict Policy, Completed-Flag-Quelle. | \`CLEAN_CUT_DATABASE.md\` MediaProgress + Native Offline Sync | Gen. Parent-FK ↔ Child-FK \`DEFERRABLE\` erzwingt genau einen Detailtyp beim Commit; \`UNIQUE NULLS NOT DISTINCT\`, composite Unit-FKs | Echte PostgreSQL-Integrationstests: parent without child, extra wrong child, rollback, concurrent revision, replay, unit mismatch; evtl. Idempotency-Tabelle |
| B04 / BLOCKER | **Work facts und Titelregeln**: alle wirklich benötigten getypten Media/Reader/Game/Music-Felder, Credit/Publisher/Narrator-Beziehungen, EditionExternalIdentity, Provider namespace und korrigierbare Provenienz. | One Work, keine EAV; Film/Anime/Books/LightNovel/Manga/Music/Game | Draft \`WorkMetadataFacts\` als typed summary, \`WorkLocalizedValues\` nicht-titel-typed, \`WorkTitles\` alleiniger Titel-Owner | Feld- und Locale-/Provenienzmatrix, Credits/EditionID, Title-Resolution-/Search-Query |
| B05 / BLOCKER | **Music/Reader/Learning/AI**: Artists/Recordings mehrfach referenzierbar, Alben als Work, ReaderContent+TextAnchor/Bookmarks, Learning/Curriculum/Term-Workflow, AI-/Translate-Operationen und persistente Daten. | Phase-A 19 Gruppen, alte MusicModels und Reader/Learning Code | Curriculum, LearningCard/FSRS, LearningUnit/Variant sowie Profile-private Learning Activity/XP/DailyGoal/Achievement sind als Entwurf ergänzt; Scope-Modes, AI/Translation und vollständige Blueprint-/Unit-Zuordnung fehlen | Neue Tabellen/FK mit echten Features, keine Medienroot-Duplikate, gewollte Feature-Ausnahmen explizit |
| B06 / BLOCKER | **Game-only und Medien-Datei-Kette**: Wie erzwingen GameRelease.WorkVersion gehört einem Game-Work, dass StoredFile/MediaAsset/WorkVersion zur selben kanonischen Work/Edition gehört? Player/Reader-/multi-disc/file roles/release hashes. | \`GameReleases.WorkVersionId\` PK/FK; WorkEditions/Versions/Assets; \`StoredFileHashes\` | Zusammengesetzte FKs sichern Edition↔Version, MediaAsset↔Work, StoredFile↔WorkVersion und GameRelease↔Datei. Zusätzliche duale Composite-FKs GameReleases(WorkVersionId,WorkId)→WorkVersions und (WorkId,GameMediaTypeId=provisorisch 7)→Works(Id,MediaTypeId) erzwingen Game-only jetzt im DRAFT | Draft-DB-Constraint per PostgreSQL-CI positiv/negativ geprüft. BLOCKER bleibt nur B01: numerische MediaTypeId(game)=7 fachlich bestätigen und mit neuem enum:byte abgleichen; keine Doppel-GameId |
| B07 / BLOCKER | **Image-Type/Target-Abdeckung**: volle Zielmenge inkl. unterstützter Reader-Medien, Cover/Poster/Banner/Chapter; Locale+Region, erlaubte Typ-Target-Paare, Source-Priority, Manual Overrides, ImageGenerationPresets nur falls Produktfeature. | \`ImageAssignments\`, \`ImageTypes\`, \`ImageGenerationRequests\` | explizit 3 Target-FKs mit num_nonnulls=1 + typisierte Pairing-Tabelle, weitere erlaubte Targets nicht erfunden | Seed-Pairings, Unique-/priority rules, Bild-Fallback-SELECT, no invalid assignment |
| B08 / BLOCKER | **Wanted/Requests/Arr/Downloads/Rules/Import**: gezieltes relationales Modell für Request-Policy, Monitoring, Releasewahl, Download-Status, Acquisition-Workflows und Import inkl. externer Migration Center. | #880 Phase E, Phase-A Domain requests_arr | \`AcquisitionRequests\` minimal; kein generischer zusätzlicher Job-Engine | Eigene FK-/Policy-/Statusmodelle pro nötiger Funktion, eine gemeinsame Operations-Queue, korrekter Auth-Scope |
| B09 / BLOCKER | **Notification-Channels/Prefs/Events**: #842 definiert NotificationSubscriptions.Enabled/Timing, NotificationSubscriptionChannels(ProfileId,Category,Channel), NotificationProfileChannels(ProfileId,Channel) und Channels InApp/Push/Email. Welche Kanäle pro Target-Profile, Digest, Zustelllog, Outbox? | PR #842 ist **offen**; keine alte Migration 1:1 kopieren | \`Notifications\` im Draft ist nur eine Inbox-Ausgangstabelle, nicht der vollständige Fachstand | Kanalpräferenz/Target-FK, gesicherte Delivery und Retry/Outbox, C# smallint Seeds, Rechte/Idempotenz |
| B10 / BLOCKER | **Plex/Jellyfin und externe Identitäten**: #920 trennt Owner-verwaltete Plex Server Grants, profilbezogene Zustimmung/Provider-Verbindungen, Login-PIN-Purpose und idempotentes resumierbares Catalog-Mapping; #937 Jellyfin Read-Adapter. Welche Grants und non-secret checkpoints in DB, welche verschlüsselten Secrets in /data? | PR #920/#937, Auth/Plex-Login vs Medienrechte | \`Providers\`, \`WorkExternalIdentities\`, \`AccountExternalLogins\` sind voneinander getrennt; MediaConnections derzeit **nicht** modelliert | Profilzustimmung, sichtbare Library-Sections, Credentials geschützt, Reconciliation/Restart, kein automatischer Watchlist/Progress Sync |
| B11 / HIGH | **Library/Media Analyse/Tracks/Detection**: MediaTechnicalAnalyses, audio/subtitle track kinds, asset-version membership, Trailer/sprite cache lifetime, Video/Audio detector results incl. successful NoMatch. | Player, Reader, Library, Detection/Timeline | Asset/File/Track + Segments/Chapters/DetectionRuns als Prototype | Typ-/Source-Seeds, extra Analysefelder, representative PlayableSelect-Query und Hot-index plans |
| B12 / HIGH | **Durable Operations/Outbox/Events/Scan**: status/retry/claim/cancellation, bounded Queue vs Direct & independent Force, Notification-Dispatch, Account-Revocation after-commit effects, retention. | #852/#880/CLEAN_CUT, #842 notifications | \`Operations\` minimal status+lease+input; OperationLogs; keine zweite Queue | Eindeutige Claim- und Retry-Tests, eventuell Outbox im gemeinsamen DB-Tx, kein NAS-Transaktions-Claim |
| B13 / HIGH | **Account-Groups**: PR #761 fügt AccountGroups und Membership hinzu, aber kein eigenständiges Berechtigungssystem. Wird dieses Feature vor Schemafreeze Bestandteil des Cutovers? | PR #761 noch offen, Owner-Wahl erforderlich | *Keine* Groups-Tabelle ins Target ohne Integration/Produktentscheid | Bei GO eigene Accounts-FK/Composite Membership, keine Shadow-Rollen; bei defer dokumentierte separate spätere Migration |
| B14 / HIGH | **Extra Config/Calendar/Discovery/Search/Settings**: bestehende Settings/Schedule/Presentation/SubtitlePolicy-, Events-/Monitoring-Daten mit eigener Fachverantwortung, ohne generische EAV-Settings; konkret relevante persistente Funktionen aufnehmen. | Live-Phase-A Domänen, Admin UI #944, neues Discovery im Worktree | Basis-Works/Locales vorhanden, aber **nicht** alle Einstellungen/Rules/Calendar gespeichert | Funktionsmatrix zeigt Source-of-Truth je Feature oder ausdrücklich reinen Derived Cache |
| B15 / TEST | **Typed Read-Queries + Indizes**: Page=1/Size25 max100, autorisierter Profilfilter vor Root-Paging, WorkCard LATERAL/JSON children, SortKey static, CoverId/BannerId/Audio/Sub languages, Continue, playable selection. | \`CLEAN_CUT_DATABASE.md §8.3/§8.4\`, Service-Contract #852 | gezielte Candidate Indexes in Draft, Trigram index only if real search | echter seeded PostgreSQL EXPLAIN (ANALYZE, BUFFERS) und SQL-result/permission/assertions; kein NAS/provider in GET |
| B16 / TEST | **Neue EF-Baseline/PG-Kompatibilität**: PostgreSQL-Syntax gegen Zielversion, deferrable circular FK + generated FK columns, raw SQL + EF ModelSnapshot, type mapping, fresh install twice; keine Alt-Kette. | Phase C nach Gate B | Die DDL 01–12 auf isolierter PostgreSQL-18-CI ausgeführt, aber keine finale EF-Baseline/Anwendungsabnahme | PSQL-Prototyp auf isolierter DB, erst nach fachlicher Freigabe EF Greenfield-Migration in C |

### Bereits als Entwurf konkretisiert, aber noch keine Produktfreigabe

Die neuen DDL-Dateien 04–06 ergänzen Monitoring/Wanted/RequestTargets und Notifications-Voreinstellungen, eine gemeinsame Curriculum-/SharedCourse-/Learner-Progress-Struktur sowie Offline-Checkpoint-Events und Provider-Verbindungen. Das reduziert die **strukturellen** Lücken in B03/B05/B08/B09/B10. Es ersetzt **nicht** die ausstehenden Entscheidungen über Monitoring-Relations, Profil- und Rollenrechte, FSRS-Karten, Anbieter-Credentials, Releases/Arr/Quality-Profile oder die tatsächlichen Notification-Sinks. Alle Typ-Seeds bleiben bis zur Abstimmung mit den echten C#-Enums offen. Insbesondere wird kein vorhandener `/data`-Checkpoint ungeprüft zusätzlich als zweite kanonische PostgreSQL-Wahrheit angelegt.

**Zusätzliche Sicherheits- und FK-Gates:** Die Curriculum-Zuordnung Exercise↔Objective wird bereits über den gemeinsamen LessonId-Composite-FK abgesichert; dagegen muss für LearnerCourseProgress, Attempts, SharedCourseContent und ActivitySessions die Zugehörigkeit zum gepinnten Blueprint jetzt zusätzlich über die Part-10-Composite-FKs strukturell erzwungen werden; die echte Negativ-/Race-/Publish-Semantik bleibt im Test-Gate. Für Wanted/Monitoring gilt: genaue Source-Semantik (Relation, Inherit, Audiobook-Edition, fehlend vs. Upgrade) muss der neue Logic-Reconciler aus Fachzustand ableiten, nicht als doppelte DB-States speichern.


### Fortschritt aus den jüngsten Integritätskorrekturen

- **B06 teilweise geschlossen:** Ein `StoredFile` kann aufgrund des Composite-FK jetzt nicht mehr als Disc-Datei eines fremden `GameRelease.WorkVersionId` eingetragen werden. `MediaAssets(Id,WorkId)` bindet das konkrete Work, und `PlaybackSessions`/`MediaPlaybackHistory` prüfen die Work-/Asset-Identität ebenfalls per FK. **Game-only MediaType ist nun mit zusätzlichem Composite-FK und provisorisch erzeugter Konstante 7 strukturell getestet.** Vor Signatur der neuen MediaTypes-Enum-/Seedwerte (B01) bleibt das Feature dennoch kein finales Gate-B-GO.
- **B05 teilweise geschlossen:** `LearningUnitKindTypes` ersetzt freies `KindKey`. Eine neue [LearningActivity/XP/Gamification-DDL 09](DATABASE_CUTOVER_PHASE_B_09_LEARNING_ACTIVITY_DRAFT.sql) bildet die verbindliche `LEARNING_GAMIFICATION.md` ab: Sessions statt Heartbeats, profilprivate TimeSlices, idempotentes SourceEvent XP, Preferences, DailyGoal-Snapshots und Achievement-Unlocks. **Kein Streak-/XP-Kontoschatten**, keine großen Learning-Events im globalen Notification-Bus. `LearningActivitySessions.LearnerCourseId+ProfileId` ist ein echter Composite-FK; Blueprint-/Curriculum-Lesson-Zugehörigkeit und zeitliche Anti-Inflations-Logik bleiben konkrete Tests.
- **B01 weiterhin offen:** Verifizierter alter Code `LearningUnitKind` Word/Sentence/Script beweist Semantik, aber die endgültigen neuen Byte-Werte sind noch nicht freigegeben. Alle 41+ Type-Tabellen benötigen ein einheitliches, verbindliches C#-/smallint-Seed-Manifest.
- **B16 weiterhin offen:** [Read-only PostgreSQL-Assertions](DATABASE_CUTOVER_PHASE_B_PG_ASSERTIONS_DRAFT.sql) existieren für kritische FKs/`citext`/`NULLS NOT DISTINCT`; kein PostgreSQL-Server wurde hierfür gestartet und keine Assertions als bestanden behauptet.

## Umsetzungsreihenfolge

**B0 – jetzt:** die 162 neuen Tabellenentwürfe aus 01–10 und diese explizite Entscheidungsmatrix reviewen; keine stillen Produktentscheidungen als fest markierten Code eintragen. **B1:** B01–B10 nach jeweils vorhandener Funktion fachlich abschließen (unabhängige Bereiche dürfen parallel ausgearbeitet werden). **B2:** zusätzliche Target-Tabellen ergänzen; sämtliche DDL und Seeds mit tatsächlichem Code und #920/#842/#761/Android/Player-Delta abgleichen. **B3:** kritische Transaktions-/FK-/Index-/Read-Queries in einer ausdrücklich isolierten, wegwerfbaren PostgreSQL-DB prüfen und alle Skripte als **ein zusammenhängendes Zielinventar** signieren. Erst dann Gate B, anschließend Phase C.

**Nicht Teil von B:** Jede alte Tabellen-/Spaltenbezeichnung in neue Struktur überführen; 2.263 Legacy-Queries neu implementieren; alle Native/Razor/Job-Clients portieren (D/E); aktives Volume/DB zurücksetzen oder Daten übernehmen (G).

**Ownerentscheidungen bündeln:** Wenn B01–B10 durch bestehende Produktanweisungen/Quellcode vollständig entscheidbar sind, fachlich dokumentieren und weitermachen. Nur **echte** Produkt-Alternativen separat zur Freigabe benennen, nicht für technische Standardfelder reflexartig fragen. Kein Gate B „grün“ trotz ungelöster Fähigkeit.

## Belegte Enum-Quellwerte

Die tatsächlichen alten C#-Werte und der korrigierte AccountRole-Entwurf stehen in [DATABASE_CUTOVER_PHASE_B_ENUM_AUDIT.md](DATABASE_CUTOVER_PHASE_B_ENUM_AUDIT.md). Das neue Target-Seed-Manifest und die Byte-Umstellung müssen in B01 gesondert bestätigt werden.

## Konkrete Korrekturen vor Freigabe

- **Learning:** `LearningCourses` (persönliche Vokabel-/FSRS-Kurse) sind fachlich etwas anderes als `LearnerCourses` (Enrollment einer veröffentlichten Curriculum-Instanz). Beide verwenden denselben `LearningUnits`- und `LearningCards`-Owner. Curriculum muss seine tatsächlichen Unit-Links und Profile-Scope-/Gamification-Daten noch nachweisbar integrieren; kein zweiter FSRS-Datenbestand.
- **Acquisition:** `WantedItems` ist eine einzige reconciled Target-Membership-Tabelle, nicht der Scheduler. `AcquisitionDownloadBindings` referenziert zusätzlich zum Request dessen **WorkId** über zusammengesetzte FK; QualityProfile-Indexer-Allowlist-Semantik (leer=unrestricted vs. explizite Blockliste) und ReleaseAttempt-/Source-Provenienz müssen freigegeben werden.
- **Schema und Sicherheit:** Texte zu geschützten Credentials in Drafts sind **nur opaque StorageKeys**, niemals echte Tokens oder Clientkontrollierte URLs. Manuelle Review der Account/Profile-/Provider-Authorization-Grenzen bleibt erforderlich.
- **Prüfung:** Die 10 Drafts enthalten 162 `CREATE TABLE`, 782 Spalten einschließlich acht nachträglicher Blueprint-Discriminator-Spalten sowie 251 FK-Deklarationen. Dies sind **nicht** die 152 alten Anwendungstabellen; **keine 1:1-Ableitung**. Ein isolierter PostgreSQL-18-CI-Bootstrap und gezielte Negativtests sind erfolgreich ([Run #38074769345](https://github.com/Juloc/Jularr/actions/runs/38074769345)); keine Live-/dev-DB geändert.

**Konkreter B05-Integritätsfortschritt:** [Part 10](DATABASE_CUTOVER_PHASE_B_10_CURRICULUM_SCOPE_DRAFT.sql) ergänzt 15 zusammengesetzte Fremdschlüssel entlang Blueprint → Level → Chapter → Lesson → Exercise sowie SharedCourse → LearnerCourse → Progress/Attempts/Activity. Damit werden verwechselte Kurs-/Übungs-IDs nicht einfach aufgrund eines existierenden Fremdobjekts akzeptiert. [Positive/negative Scratch-DB-Tests](DATABASE_CUTOVER_PHASE_B_PG_NEGATIVE_TESTS.sql) prüfen die Beziehung zusätzlich; daraus folgt noch keine fertige Publishing-/Scope-/Rollenberechtigung.

**Prüfnachweis:** [GitHub Actions Scratch PostgreSQL 18.6](https://github.com/Juloc/Jularr/actions/runs/38073814369) hat die Blueprint-FKs und acht gezielte Positiv-/Negativtests nach echtem SQL-Bootstrap bestanden. Das löst **nur die verifizierten relationalen Teilregeln**, nicht das gesamte B05-/B16-Gate: Seed-Manifest, Publishing/FSRS/Rollen, Progress-Offlineraces, Leistungstests und EF-Baseline bleiben ausdrücklich offen.

**B01 ist jetzt vollständig als Type-Katalog inventarisiert, aber nicht fachlich abgeschlossen:** [ENUM_SEED_MANIFEST.csv](DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv) führt 41 Typ-Tabellen, davon fünf mit im SQL vorgeschlagenen Werten. Für die restlichen 36 müssen die benötigten Keys/Byte-Werte verbindlich aus den neuen Service/Logic-Domänen festgelegt werden; kein automatisch übernommener Alt-Enumwert. **Keine gesonderte Type-Tabelle für reines UI-Vokabular ohne persistierten FK erfinden.**

## B12/B15 – gezielte zusätzliche SQL-Testbelege

- **B15 Continue:** Die unveränderte Service-SELECT-Struktur aus `DATABASE_CUTOVER_PHASE_B_READ_QUERIES_DRAFT.sql` wird als typisierte Prepared Query mit zwei getrennten Testprofilen ausgeführt. Die Tests sichern Reihenfolge, Offset, Profiltrennung und das Ausblenden abgeschlossener Einträge; ein `EXPLAIN (ANALYZE, BUFFERS)` prüft die tatsächliche PostgreSQL-Ausführung. Dies ersetzt **nicht** Query-Plan-Belege für alle fachlichen Lastprofile und Autorisierungs-Gates.
- **B12 Operations:** Exakt die bestehende Logic-only-Claim-Query wird in **zwei PostgreSQL-Sessions** ausgeführt: ein gesperrter Pending-Job muss übersprungen werden, freie fällige Jobs dürfen je einmal geclaimt werden, ein zukünftiger Job bleibt Pending, nach Freigabe wird der zuvor gesperrte Job genau einmal geclaimt. Die Testumgebung ist die **wegwerfbare** CI-DB `phase_b_scratch`. Dieser Claim-Test ersetzt **nicht** endgültige Retry-/Cancellation-/Outbox-Produktverträge.
- Beide Belege gehören zu PR #948, einem ausdrücklich separaten Teil-PR in die Phase-B-Branch, und schließen weder Gate B noch den späteren Cutover vorzeitig.


## Stand nach Learning-Scopes (13) und öffentlichen IDs (14)

**Korrigierter DDL-Zwischenstand:** 14 SQL-Abschnitte, **173 Target-Tabellen** und **46 Type-Kataloge**, noch keine finale Baseline. Die fünf Learning-Zieltabelle und drei Type-Kataloge sind in TABLE_MANIFEST/ENUM_SEED_MANIFEST ergänzt. Neu hinzugekommene `PublicId uuid`-Spalten auf 16 tatsächlich nach außen adressierbaren Entitäten werden im selben Tabellenmanifest geführt; **alle** internen `Id`-/FK-/Composite-Key-`bigint` bleiben erhalten. PostgreSQL wird mit einem isolierten positiven/negativen Public-ID-Test validiert.

**Normativer Owner-Vorrang:** öffentliche Ressourcenidentität ist nicht die SQL-`Id` und nicht das Sicherheitstoken. Die Regel `Works.Id bigint` bleibt intern; `Works.PublicId uuid` ist die öffentliche Adresse. Login-/Recovery-/Refresh-/Invite-/Pairing-/Capability-Secrets sind kryptografisch zufällige Tokens mit Hash/Secret-Store, Scope, Expiry, Rotation und Revocation. Provider-IDs bleiben externe Namespaces. Alte numerische Client-Routen werden erst koordiniert in **Phase E** ersetzt; kein ungetestetes Breaking-Change in der alten laufenden dev-Version.

Die Client-/API- und echte Service-Berechtigungsnachweise werden in D/E getestet. Public UUIDs bieten keine Zugriffsberechtigung. **Keine** künstliche Permission-Proxy-Tabelle und **kein** SQL-Join über PublicId.

**Noch offen:** die restlichen Typ-/Seed-Verträge (B01), finale Profile-/Auth-/AI-/Arr-/Provider-/Import-Verträge und integrale Query-/Permission-Lasttests. Die Anzahl 173 ist kein festgeschriebener Sollwert und kein Gate-B-Abnahmebeweis. EF-Baseline gehört nach Gate B in Phase C.

## B03/B15 – Account-Scope und öffentliche Read-Referenzen

Watchlist und Continue prüfen den serverseitigen `ActorAccountId` gegen
`AccountProfiles` und einen aktivierten `Accounts`-Datensatz im selben statischen
SQL, vor der Pagination. Der Progress-CAS prüft dieselbe aktuelle Mitgliedschaft
vor dem Update. Profilfreigabe, Freigabeentzug, deaktivierter Account und fremder
Account mit gültiger Profil-ID sind als tatsächliche Rückgabe-/Affected-row-Fälle
geprüft; die Service-/Permission-Gates aus D bleiben zusätzlich erforderlich.

Beide outward SELECTs liefern öffentliche UUIDs für Work, Bilder, Progress und
optionale Units. `WorkTracks`, `WorkEditions` und `MediaProgress` erhalten dazu
ebenfalls `PublicId`; interne Schlüssel und sämtliche Joins bleiben `bigint`.
Die insgesamt 19 PublicId-Verträge verändern weder Tokens noch die Tabellenzahl.
Die psql-PREPAREs werden lokal und in CI durch denselben
`scripts/prepare-phase-b-queries.mjs` aus den kanonischen SELECTs erstellt.

**Lokaler Nachweis:** PostgreSQL 18.6, isolierter Container ohne veröffentlichte
Ports oder persistente Volumes. Zwei frische Datenbanken mit DDL 01–14 aufgebaut;
die zweite enthält alle Änderungen dieses Abschnitts. Katalog-, Negativ-, Event-,
WorkCard-, Continue-, Acquisition-, Progress-CAS-, Learning- und Public-ID-Suites
bestanden. Worker-SKIP-LOCKED mit zwei getrennten Sessions ebenfalls bestanden.
EXPLAIN ANALYZE/BUFFERS für 1.000 synthetische Watchlist-Roots und Continue liegt
vor; das ist noch keine repräsentative Lastabnahme. Gate B bleibt offen.

## B03/B11 – Reader-Work und Dimensionspaare

`ReaderContent(WorkEditionId, WorkId)` ist jetzt durch Composite-FK an die
Edition gebunden. `ReadingProgressPositions(MediaProgressId, WorkId)` referenziert
den tatsächlichen Progress-Work; `(ReaderContentId, WorkId)` referenziert denselben
Content-Work. Fremdcontent, falsch bezeichnete Editions und spätere Work-Wechsel
werden relational abgelehnt. Keine neue Reader-/Progress-Wahrheit und kein Trigger.

Die Dimensions-CHECKs von Images und MediaTechnicalAnalyses erlauben entweder
zwei unbekannte Werte oder zwei strikt positive bekannte Werte. Ein einzelner
NULL-Wert besteht den CHECK nicht mehr durch die SQL-UNKNOWN-Semantik.

**Lokaler Nachweis:** DDL 01–14 erneut vollständig auf einer frischen isolierten
PostgreSQL-18.6-DB aufgebaut. Alle bisherigen SQL-Suites plus Reader-Integrität
bestanden. Die neue Suite prüft vier spezifische Cross-Work-FK-Ablehnungen,
zulässigen Contentwechsel innerhalb desselben Works sowie je sechs ungültige
Dimensionspaare gegen beide Tabellen. Constraint-Namen werden geprüft.
Gate B bleibt offen für die übrigen Fach-/Seed- und vollständigen Query-Verträge.
