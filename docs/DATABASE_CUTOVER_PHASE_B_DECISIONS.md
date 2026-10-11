# Phase B — verbleibende Gates ohne Learning

Status: OPEN. Maßgeblich ist der aktuelle Owner-Auftrag vom 10.10.2026:
Cutover ohne Learning. Frühere Learning-Blocker sind bewusst aus dem Scope
genommen, nicht technisch als fertig erklärt.

Aktueller Entwurf: dreizehn DDL-Dateien, 143 Tabellen, 31 feste Type-Kataloge mit
160 expliziten Byte-Codes und 24 öffentlichen Ressourcen-UUIDs. Counts sind
Inventarkontrollen, keine Abnahmeziele. Kein Eingriff in aktive DBs oder Runtime.

## Aktuelle Abnahmematrix

| ID | Stand | Vor Gate B erforderlicher Nachweis |
| --- | --- | --- |
| B01 Types/Seeds | Alle bestehenden Kataloge definiert; keine freien Legacy-int-Mappings | C# byte, Manifest und statische Seeds identisch; neue erforderliche Kataloge erhalten denselben Nachweis |
| B02 Account/Profile/Auth | Zweckgebundene Challenges, Account-Epoch, Rotation/Revoke, TOTP-/Recovery-Replay und Transfer physisch geprüft | Plex-Login/Linking und Medien-Consent nutzen getrennte feste Zwecke; Provider-Capability-Default-off, Grant-Lifecycle und Zugriffstests in B10 ergänzt; WebAuthn-Protokollprüfung gehört in D |
| B03 Progress/Offline | Atomare Root-/Subtype-Erzeugung, gemeinsamer Time/Reader/Game-Checkpoint samt unveränderlichem Hash-Beleg, aktuelle Membership/CAS, Edition-/Contentrevision, explizite Completion/Unmark, SQL-CompletedThrough und zwei echte konkurrierende Accounts geprüft | Vollständige getypte Reflow-/FixedPage-/Variant-Anker einschließlich Bookmarks/Highlights in B05; unterstützte Offline-/Replay-Retention in B12 abschließen |
| B04 Work Facts/Titel | Ein Work; explizite MetadataFacts, Titelprovenienz, Namespaces | Vollständige Medien-/Edition-/Credit-/Locale-Fakten und statische Title-Resolution-/Search-Queries |
| B05 Music/Reader/AI | Keine Learning-Anforderung mehr; übrige Funktionen bleiben | Artists/Recordings, Reader-Inhalte/Bookmarks/Highlights sowie AI-/Translate-Persistenz vollständig; keine parallelen Roots |
| B06 Games/File Chain | Game-only und Work/Version/Asset/File-Composite-FKs vorhanden | Genuine Plattform-/Release-/Multidisc-/Hash-Funktionen erhalten; Typ 7 ist jetzt expliziter Game-Byte-Vertrag |
| B07 Images | Feste ImageKinds und drei typisierte Target-FKs | Tatsächlich benötigte Targets/Pairings, Season-Artwork, Locale/Region/Manual-Override und Bild-Fallback-Query |
| B08 Wanted/Arr/Acquisition/Import | Regeln, Profile, Indexer, Download-Bindings, Wanted-Coverage vorhanden | Request-/Monitoring-Policies, ReleaseAttempts/Provenienz, Import/Reconciliation und geplantes Migration Center |
| B09 Notifications | Inbox, Quiet Hours/DST, Digest, Endpoints, Route-Revalidierung und parallele Gruppierung physisch geprüft | Reale Sink-/Secret-Konfig-Verträge und Recovery bei ungewissem externen Send-Ausgang; keine Fake-Verfügbarkeit |
| B10 Provider | Default-off Flags kanonisch in Providers; Consent-/Grant-Revisions, Owner/Sections, Operation-Lease und atomare Scan-Generation/Cursor physisch geprüft | Zielvertrag für Secret-Staging/Revoke und exakten User-ACL-/Work-Abgleich definiert; kontrollierte echte Plex/Jellyfin-Adapter-/Client-Prüfung folgt in D/F; keine automatischen Watchlist-/Progress-Imports |
| B11 Media/Detection | Feste Track/Asset/Detector-Codes; konsistente Run-Ergebnisse | Probe-Invalidation/Analysefelder, Cache-Lifecycle, PlayableFile-SELECT und passende Indexpläne |
| B12 Operations/Events | Gemeinsame Operations, Leases/Claims und Delivery-Versuche vorhanden | Retry/Cancellation/Restart/Retention sowie benötigte After-Commit-/Outbox-Effekte; keine externe I/O im DB-Tx |
| B13 Account Groups | Neutrale Gruppen, Mitgliedschaft, UUID und Owner-Paging vorhanden | Gruppen bleiben Policy-Selektoren, keine Rollen oder automatische Rechte; D/E implementiert Verbraucher/UI |
| B14 Config/Calendar/Discovery | Keine Learning-Einstellungen im Target | Alle übrigen echten Settings/Rules/Schedules/Discovery-Fakten ihrem kanonischen Owner zuordnen; kein EAV |
| B15 Typed Reads/Indizes | 31 getypte statische Queries: Watchlist/Continue/Groups/Inbox/Auth/Provider/Reader/Request-History, Claims und atomare Logic; Continue paged vor Positions-Enrichment, exakter Progress-Snapshot und Completion-Präfix in SQL; 1000er Continue-Paging und parallele Progress-Writes geprüft | Weiter fehlende Reader-Content-Delivery-/Playable-/Search-/Arr-Detail-/Jobs-Reads und repräsentative EXPLAIN-/Berechtigungsbelege |
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
