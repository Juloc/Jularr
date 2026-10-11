# Phase B — Music-Identität und wiederverwendbare Recordings

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

## Recording und Reads

Ein Recording kann in mehreren Album-Track-Platzierungen vorkommen. Die
WorkTrackRecordings-Beziehung ist optional durch Abwesenheit; ein unaufgelöster
Track erhält kein erfundenes Recording. Track-Titel bleiben Placement-Fakten,
Recording-Titel und DurationMs gehören zum Recording. Dateien werden nicht hier
gespeichert. Referenzierte Identitäten/Recordings werden nicht kaskadierend gelöscht.

[Drei statische Service-Reads](DATABASE_CUTOVER_PHASE_B_MUSIC_QUERIES_DRAFT.sql)
verwenden aktuelle enabled Accounts und Profile-Membership, UUID-Adressen und
explizit gebundene long/UUID/int-Parameter. Die Listen verwenden Page 1, Size 25,
maximal 100, checked Offset maximal 100000 und eindeutige Sortierung. Data validiert
PageRequest vor SQL; PostgreSQL lehnt negative OFFSET zusätzlich explizit ab.
Recording-Roots werden vor Enrichment paginiert. Kein GET schreibt oder ruft
MusicBrainz, NAS oder einen Importer auf. Music-Modul-/Capability-Gates werden im
kanonischen Service in D portiert; keine aus AddedByProfileId erfundene Privatbibliothek.

[Physische Tests](DATABASE_CUTOVER_PHASE_B_PG_MUSIC_IDENTITY_TESTS.sql) prüfen
Solo-/Ensemble-Identität, gemeinsame Credits, Provider-Namespaces/Quote-Parameter,
ein Recording auf zwei Alben, Rollback/FK/Unique-Rejection, Profile-/Artist-Isolation,
Sharing/Entzug/Disable, stabile Tie-Breaker und 1000er Paging. EXPLAIN belegt
Artist-Sortindex, einmalige Autorisierung, Recording-Credit-Index vor LIMIT und
nur die ausgewählten Recording-PK-Lookups.

B05 bleibt offen für Album-/Release-/Disc-Fakten, Artist-Monitoring und AI/Translate.
Monitoring/Requests nutzen in B08 den gemeinsamen Owner; keine Artist-eigene Queue.
Dieser Nachweis ist keine vollständige Music- oder Gate-B-Abnahme.
