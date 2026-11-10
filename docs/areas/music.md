# Music

Albums as Works, MusicBrainz metadata and music acquisition.
Index: [CODEMAP](../CODEMAP.md).
Specs: [PARITY_LIDARR](../PARITY_LIDARR.md).

## Backend

- Music in Arr+: an album is the Work.
  [MusicBrainzClient.cs](../../src/Jularr.Integrations/Logic/MusicBrainz/MusicBrainzClient.cs) (release groups, tracks of the earliest official
  edition, one request per second) and [MusicMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/MusicMetadata.cs) (artists as people, a
  track's own artists on its unit, Cover Art Archive poster, tracks as units, `musicbrainz` identity);
  [MusicReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/MusicReleaseJudge.cs) (artist and album as separate word runs with accents and
  apostrophes folded, compilations as VA, other editions and sequels to review, FLAC/ALAC/MP3/AAC qualities); placement
  `Artist/Album (Year)/NN - Title.ext` in tag order (disc and track, tagged titles name the files;
  [AudioTagReader.cs](../../src/Jularr.Integrations/Logic/Media/AudioTagReader.cs) with TagLibSharp) or else natural order, tied to track units only
  when the file count matches the edition; a music library scan assigns unassigned files tagged with an album's MusicBrainz release group to that
  album, importing up to 50 new albums per scan
  ([MusicTagIdentification.cs](../../src/Jularr.Backend/Service/System/Operation/V1/MusicTagIdentification.cs)); with an AcoustID key switched on and
  `Media:FpcalcPath`, untagged folders are fingerprinted ([AudioFingerprinter.cs](../../src/Jularr.Integrations/Logic/Media/AudioFingerprinter.cs),
  [AcoustIdClient.cs](../../src/Jularr.Integrations/Logic/AcoustId/AcoustIdClient.cs), 200 files per scan) and go to the one album all their files
  match ([MusicFingerprintLogic.cs](../../src/Jularr.Backend/Logic/Library/MusicFingerprintLogic.cs)).
  The playback item names the previous and next present unit of the same Work, so an album or season continues in order; `ReadPersonV1`,
  `ReadPersonWorksV1` and `/People/{id}` list a person's titles.
