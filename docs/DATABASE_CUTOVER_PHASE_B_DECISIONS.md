# Phase B — verbleibende Gates ohne Learning

Status: OPEN. Maßgeblich ist der aktuelle Owner-Auftrag vom 10.10.2026:
Cutover ohne Learning. Frühere Learning-Blocker sind bewusst aus dem Scope
genommen, nicht technisch als fertig erklärt.

Aktueller Entwurf: elf DDL-Dateien, 139 Tabellen, 29 feste Type-Kataloge mit
154 expliziten Byte-Codes und 22 öffentlichen Ressourcen-UUIDs. Counts sind
Inventarkontrollen, keine Abnahmeziele. Kein Eingriff in aktive DBs oder Runtime.

## Aktuelle Abnahmematrix

| ID | Stand | Vor Gate B erforderlicher Nachweis |
| --- | --- | --- |
| B01 Types/Seeds | Alle bestehenden Kataloge definiert; keine freien Legacy-int-Mappings | C# byte, Manifest und statische Seeds identisch; neue erforderliche Kataloge erhalten denselben Nachweis |
| B02 Account/Profile/Auth | Zweckgebundene Challenges, Account-Epoch, Rotation/Revoke, TOTP-/Recovery-Replay und Transfer physisch geprüft | Plex-Login/Linking und Medien-Consent nutzen getrennte feste Zwecke; Provider-Capability-Default-off und Zugriffstests in B10 ergänzt; Grant-Lifecycle weiterhin abschließen; WebAuthn-Protokollprüfung gehört in D |
| B03 Progress/Offline | Total-Subtype, Cross-Work, Revision-CAS und Replay modelliert/getestet | Reader-Anchor-/Editionrevisionen, Completed-Provenienz und repräsentative Konflikt-/Concurrency-Fälle vollständig |
| B04 Work Facts/Titel | Ein Work; explizite MetadataFacts, Titelprovenienz, Namespaces | Vollständige Medien-/Edition-/Credit-/Locale-Fakten und statische Title-Resolution-/Search-Queries |
| B05 Music/Reader/AI | Keine Learning-Anforderung mehr; übrige Funktionen bleiben | Artists/Recordings, Reader-Inhalte/Bookmarks/Highlights sowie AI-/Translate-Persistenz vollständig; keine parallelen Roots |
| B06 Games/File Chain | Game-only und Work/Version/Asset/File-Composite-FKs vorhanden | Genuine Plattform-/Release-/Multidisc-/Hash-Funktionen erhalten; Typ 7 ist jetzt expliziter Game-Byte-Vertrag |
| B07 Images | Feste ImageKinds und drei typisierte Target-FKs | Tatsächlich benötigte Targets/Pairings, Season-Artwork, Locale/Region/Manual-Override und Bild-Fallback-Query |
| B08 Wanted/Arr/Acquisition/Import | Regeln, Profile, Indexer, Download-Bindings, Wanted-Coverage vorhanden | Request-/Monitoring-Policies, ReleaseAttempts/Provenienz, Import/Reconciliation und geplantes Migration Center |
| B09 Notifications | Inbox, Quiet Hours/DST, Digest, Endpoints, Route-Revalidierung und parallele Gruppierung physisch geprüft | Reale Sink-/Secret-Konfig-Verträge und Recovery bei ungewissem externen Send-Ausgang; keine Fake-Verfügbarkeit |
| B10 Provider | Unabhängige Login-/Link-/Media-/Autoprovision-Flags default-off; aktuelle Profilzugehörigkeit und Provider-Freigaben statisch geprüft; Connections und Grants vorhanden | Plex/Jellyfin Consent/Owner/Sections, Credential-Revision, Restart/Reconciliation sowie realer Provider-Abgleich; keine automatischen Watchlist-/Progress-Imports |
| B11 Media/Detection | Feste Track/Asset/Detector-Codes; konsistente Run-Ergebnisse | Probe-Invalidation/Analysefelder, Cache-Lifecycle, PlayableFile-SELECT und passende Indexpläne |
| B12 Operations/Events | Gemeinsame Operations, Leases/Claims und Delivery-Versuche vorhanden | Retry/Cancellation/Restart/Retention sowie benötigte After-Commit-/Outbox-Effekte; keine externe I/O im DB-Tx |
| B13 Account Groups | Neutrale Gruppen, Mitgliedschaft, UUID und Owner-Paging vorhanden | Gruppen bleiben Policy-Selektoren, keine Rollen oder automatische Rechte; D/E implementiert Verbraucher/UI |
| B14 Config/Calendar/Discovery | Keine Learning-Einstellungen im Target | Alle übrigen echten Settings/Rules/Schedules/Discovery-Fakten ihrem kanonischen Owner zuordnen; kein EAV |
| B15 Typed Reads/Indizes | Watchlist/Continue/Groups/Inbox/Auth, Reader-Bookmarks/-Highlights/-Pages sowie Claim/CAS, Auth- und Inbox-Logic statisch; profilautorisierte Reader-Tests + EXPLAIN ergänzt | Weiter fehlende Reader-Content-Delivery-/Playable-/Search-/Arr-/Jobs-Reads und repräsentative EXPLAIN-/Berechtigungsbelege |
| B16 Physische DDL | Nur isolierter PostgreSQL 18; aktuelle Fassung erneut prüfen | Reproduzierbarer frischer Bootstrap, Seeds/FKs/CHECKs und fokussierte Integritäts-/Rollback-/Isolationstests |

## Verbindliche Grenzen

- Ein kanonischer Work einschließlich Game; Anime ist Klassifikation, Audiobook
  ist Edition/Format. Kein zweiter Progress-/Artwork-/Acquisition-/Event-Owner.
- Ressourcen intern bigint/C# long; outward UUID separat und ohne Rechtewirkung.
  Tokens sind sichere, zweckgebundene Secrets, nicht bloß GUID-Adressen.
- Service autorisiert Reads und orchestriert; Data beschreibt Verträge;
  nur Logic schreibt. Keine neue Store-Schicht.
- Outward-Collections sind vor Root-Paging autorisiert: Page 1, Size 25,
  maximal 100, Offset maximal 100000, eindeutiges ORDER BY, LIMIT/OFFSET.
  Keine NAS-/Provider-Aufrufe, N+1 oder Whole-Dataset-Filter im GET.
- Persistierte geschlossene Enums sind explizite byte/smallint-Seed-/FK-Verträge.
  WorkFactTypes beschreibt nur Provenienz der neun getypten Fakten, kein EAV.

## Zurückgestellt und nachgelagert

[Learning](DATABASE_CUTOVER_DEFERRED_LEARNING.md) bleibt nur als grober späterer
Ausbau. Keine Learning-Baseline, Reservefelder, Fallbacks oder Testgates. Der
bestehende Code wird in D/E aus der Cutover-Runtime kohärent entfernt.

C erzeugt erst nach Gate B die einzelne frische EF-Baseline. D/E portiert alle
verbleibenden Owner/Clients/Worker und entfernt ersetzte Pfade. F ist die
Gesamtvalidierung; G benötigt separate exakte DB-Reset-Freigabe.
Diese Schritte werden nicht als erledigt oder als Phase-B-Blocker verwechselt.
