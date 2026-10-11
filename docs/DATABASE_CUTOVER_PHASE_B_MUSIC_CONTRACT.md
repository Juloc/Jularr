# Phase B — Music-Identität, Releases und Track-Platzierungen

Isolierter Zielvertrag, keine Runtime-Portierung. Music-Alben bleiben Works,
Track-Platzierungen WorkTracks, Dateien MediaAssets/StoredFiles. Keine MusicWorks,
zweite Dateikette oder provider-/titelbasierte Ersatzidentität.

## Artist und Credits

People ist der gemeinsame Owner einer kreditierbaren Identität, auch eines
Ensembles. Der Name bezeichnet den kreditierten Act, nicht zwingend eine natürliche
Person. MusicArtists.Id ist derselbe bigint-PK/FK, keine zweite Identity. Nur
Music-Lifecycle-Fakten stehen in der Extension: AddedByProfileId, CreatedAt und
LastRefreshedAt. Namen und Sortierung werden einmal in People gespeichert.
Artist-Adressen verwenden People.PublicId UUID; Recording-Adressen eigene UUIDs.
Keine UUID wird relationaler FK, Provider-ID oder Berechtigungsnachweis.

WorkMusicArtists und MusicRecordingArtists speichern Beziehungen mit geordneter
CreditName-Provenienz. CreditName ist der konkrete Name auf diesem Album/Recording,
kein parallel gepflegter Artist-Name. Die gemeinsame People-Identität kann zugleich
Edition-Credits tragen. Gleiche Namen dürfen verschiedene Artists bezeichnen;
kein automatischer Merge nach Name.

PersonExternalIdentities und MusicRecordingExternalIdentities lösen einen
Provider-/Namespace-/ExternalId-Schlüssel eindeutig auf. MusicBrainz Artist- und
Recording-IDs bleiben externe Strings, nicht unsere PublicId. Pro Objekt und
Provider-Namespace existiert genau eine aktuelle Zuordnung; eine Korrektur wird
vom autorisierten Metadata-Logic-Owner explizit geschrieben. Keine Namensheuristik.

## Album, Release und Recording

MusicAlbums erweitert ausschließlich Music-Works (Typ 6), ohne zweiten Work-Root.
Sieben neue feste Albumtyp-Codes unterscheiden Album, EP, Single, Compilation,
Live, Soundtrack und Other. Veröffentlichungsdatum bleibt in WorkMetadataFacts;
ReleaseGroup-Identität in WorkExternalIdentities. Ein konkretes Release ist eine
WorkEdition mit eigener EditionExternalIdentity, kein zweites Album. Die gewählte
TrackListWorkEditionId muss zum selben Work gehören.

MusicTrackPlacements erweitert einen WorkTrack um genaue Release-/Disc-/Track-
Zuordnung, optionale Placement-Dauer und optionales MusicRecordingId. Composite-
FKs sichern denselben Work für Track und Release; Edition/Disc/Track ist eindeutig.
Ein Recording kann auf mehreren Alben oder Releases vorkommen. Unaufgelöste
Recordings bleiben NULL; keine erfundene Provideridentität. Track-Titel bleiben
in WorkTracks, Recording-Titel und Recording-Dauer in MusicRecordings. Die
unvollständige alte Recording-Brücke ist entfernt, nicht als Fallback behalten.
Referenzierte Identitäten/Recordings werden nicht kaskadierend gelöscht.

Physische Zuordnung zu MediaAssets/StoredFiles und Probe-/Cache-Lifecycle bleiben
in B11 offen. Diese Faktenprüfung ist kein vollständiger Import-/Playback-Nachweis.

## Reads und Monitoring

[Fünf statische Service-Reads](DATABASE_CUTOVER_PHASE_B_MUSIC_QUERIES_DRAFT.sql)
verwenden aktuelle enabled Accounts und Profile-Membership, UUID-Adressen und
explizit gebundene long/UUID/int-Parameter. Die Listen verwenden Page 1, Size 25,
maximal 100, checked Offset maximal 100000 und eindeutige Sortierung. Data validiert
PageRequest vor SQL; PostgreSQL lehnt negative OFFSET zusätzlich explizit ab.
Recording-/Album-/Placement-Roots werden vor Enrichment paginiert. Kein GET schreibt oder ruft
MusicBrainz, NAS oder einen Importer auf. Music-Modul-/Capability-Gates werden im
kanonischen Service in D portiert; keine aus AddedByProfileId erfundene Privatbibliothek.

[Physische Tests](DATABASE_CUTOVER_PHASE_B_PG_MUSIC_IDENTITY_TESTS.sql) prüfen
Solo-/Ensemble-Identität, gemeinsame Credits, Provider-Namespaces/Quote-Parameter,
ein Recording auf zwei Alben, Rollback/FK/Unique-Rejection, Profile-/Artist-Isolation,
Sharing/Entzug/Disable, stabile Tie-Breaker und 1000er Paging. EXPLAIN belegt
Artist-Sortindex, einmalige Autorisierung, Recording-Credit-Index vor LIMIT und
nur die ausgewählten Recording-PK-Lookups.

Der [gemeinsame Monitoring-Vertrag](DATABASE_CUTOVER_PHASE_B_MONITORING_CONTRACT.md)
definiert Artist All/None/Future als kurze atomare Commands mit Operations-Beleg.
Keine gespeicherte Future-Mode, kein Music-eigenes Wanted oder zweiter Scheduler.
Lifecycle-/Placement-/Paralleltests prüfen All/None/Future, später entdeckte Alben,
identischen Retry, geändertes Payload, Entzug/Disable, Rollback, vier Relation-
Quellen, explizite Off-Priorität, erhaltene Track-Entscheidungen und Cross-Work-
Rejection. Dieselbe Person kann mehrere konkrete Crew-Rollen auf einer Edition
haben; exakte doppelte Credits einschließlich NULL-Rolle bleiben verboten.
EXPLAIN auf 1000 Alben/Tracks belegt LIMIT vor den Detail-Lookups. Drei wirklich
überlappende Sessions prüfen Command/Retry/Metadata unter demselben Artist-Mutex.

B05 bleibt offen für AI/Translate. B08 behält allgemeine Monitoring-Commands,
Requests und Wanted-/Import-Reconciliation; B12 Operations-Retention/After-Commit.
Dieser Nachweis ist keine vollständige Music- oder Gate-B-Abnahme.
