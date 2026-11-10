# Arr+ administration pages

Quality profiles, custom formats, indexer and download-client pages.
Index: [CODEMAP](../CODEMAP.md).
Specs: [PARITY_PROWLARR](../PARITY_PROWLARR.md).

## Decisions

Owner decisions; change them only with the owner.

- The quality rules are three tabs without a media-type bar: **Profiles** (one list of every profile; each names the media types it applies to as
  chips, *Applies to*, and those it is the default of, *Default*; every media type has exactly one default, Anime one for anime films and series),
  **Custom formats** (once for every media type, each profile scores them; the release profiles, release groups and tags are sections under them) and
  **Quality** (the quality sizes as one slider per quality from its minimum over the preferred to its maximum size, the end of the track meaning no
  limit; films, series, books per volume).
  A profile joins only media types of one quality list (films, series and anime; books and light novels; manga; music; games).
  A new profile starts from its media type (`?new=profile`, then `/New?kind=`); addresses of the former panes and of the media-type bar lead to the
  tab that holds them now.
- The indexers and the subtitle providers are a compact `ListPanel` under their figures: name with a kind tag (Usenet, Torrent, Books; a subtitle
  provider its service) and host, what it serves, the state chip, today's queries as a thin bar against the daily limit with the grabs ("No limit"
  without one; not for subtitle providers), priority, and Test and Edit at the row's end; a phone shows each row as a card with these facts, the host
  one line beside the kind that ends in an ellipsis (whole on hover and in the dialog), never wrapped letter by letter.
  Every setting (address, key, priority, state, media types, categories, search mode, limits) is in the edit dialog the name or Edit opens
  (`&edit=true`, Cancel and Save), whose head shows the state and the last test with Test (the dialog stays open), Search and Delete.
  Download clients, release profiles and tags (and the indexers of the setup) are wide `FeatureCard`s whose settings are inputs (`form.Save()`, saved
  by the page's pill; a key is masked and never echoed); the switch, test and search are direct actions, and on the connection pages the figures and
  cards are one `Region`.
  A `Modal` that the address opens (`?new=`, `?Type=`, `?delete=true`) with the list's address as `returnTo` is only for adding (with `form.Cancel()`
  and `form.Submit(...)`), for confirming a removal and for tools (the test search); the custom format keeps its dialog because its conditions need
  the room.
  There is no delay, waiting or minimum-age option on any acquisition page.

## Backend

- Arr+ administration: [profile services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionProfileServices.cs) (owner decision: one
  list of profiles; a profile applies to media types of one quality list, `AcquisitionMediaKind` and `QualityFamily` in
  [AcquisitionMediaKind.cs](../../src/Jularr.Backend/Data/Enums/Acquisition/AcquisitionMediaKind.cs): films, series and anime share Radarr's list, a
  series remux is named `Remux-1080p` like a film's; books and light novels, manga, music, games each their own; tables `AcquisitionProfileMediaKinds`
  and `AcquisitionProfileDefaults` (exactly one default per type, Anime one for anime films and series),
  [0042](../../database/migrations/0042_shared_quality_profiles.sql), which merged profiles of one name and the same settings into one for all their
  types (others of one name name their type, "Deutsch (Filme)") and wrote the old profile onto every target whose new default would differ; every
  query takes a target's profile from the view `WantedItemProfiles` (its own when it applies to the Work's type in `WorkAcquisitionKinds`, else the
  type's default); [AcquisitionProfileLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/AcquisitionProfileLogic.cs);
  `AcquisitionTests.Profiles_OneDefaultPerType_*`, `DatabaseMigrationTests.SharedQualityProfiles_*`), [custom
  formats](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/CustomFormatServices.cs) (once for every media type, one name in any case; each
  profile scores them), [JSON import](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/CustomFormatImportServices.cs), [quality
  sizes](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/QualityDefinitionServices.cs) (books per volume by kind,
  [0025](../../database/migrations/0025_book_volume_sizes.sql)), [release/tag
  policies](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionPolicyServices.cs), [release
  groups](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/ReleaseGroupServices.cs) (collected by every search, one list rated for every media
  type; [ReleaseGroupLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/ReleaseGroupLogic.cs),
  [0028](../../database/migrations/0028_release_groups.sql), [rules](../AUTOMATIC_RELEASE_SELECTION.md#release-groups); each profile's good and bad
  group points are saved with the profile, `AcquisitionTests.Profiles_ReleaseGroupScores_*`); matching in
  [CustomFormatMatcher.cs](../../src/Jularr.Backend/Logic/Acquisition/CustomFormatMatcher.cs).
  Focused tests: AcquisitionPolicyTests.cs, ArrAdminTests.cs, ArrQualityTests.cs, ReleaseGroupTests.cs, `AcquisitionTests.ReleaseGroups_*`.

## Frontend

- [Acquisition](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.cshtml.cs) (`/Admin/Acquisition`, `/Downloads`, `/Subtitles`: indexers,
  download clients and subtitle providers, one page each; indexers and subtitle providers are the figures over a compact list
  ([Acquisition.Indexers.cs](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.Indexers.cs): name with its kind tag, what it serves,
  state, today's queries as a bar against the limit, priority, Test and Edit) whose edit dialog (`&edit=true`) holds every setting with the state, the
  last test, Test, Search and Delete; the setup keeps a wide card per connection with its settings saved by the pill; Add (a dialog with Cancel),
  Prowlarr import and paging in the dock; deleting and the test search are dialogs opened by the address; `DownloadPagesTests`),
  [Sources](../../src/Jularr.Frontend/Pages/Admin/Sources/Sources.cshtml.cs), [Quality
  rules](../../src/Jularr.Frontend/Pages/Admin/Profiles/Profiles.cshtml.cs) (the Quality profiles entry of the Acquisition group,
  `/Admin/Acquisition/Profiles?tab=` in three tabs, owner decision, [DownloadPages](../../src/Jularr.Frontend/Framework/DownloadPages.cs) (also where
  the former panes' and media-type addresses lead): the profiles as one list with their media types and defaults as chips (a new one starts from its
  type in a dialog), the custom formats once for every type with the release profiles, release groups
  ([Profiles.Groups.cs](../../src/Jularr.Frontend/Pages/Admin/Profiles/Profiles.Groups.cs): a find-or-add field and one segmented rating per group
  that saves on click) and tags as sections ([Profiles.Panes.cs](../../src/Jularr.Frontend/Pages/Admin/Profiles/Profiles.Panes.cs); the format's
  conditions stay in a dialog, release profiles and tags are cards with their settings inline, adding is a dialog), and the quality sizes as sliders
  (`Form.SizeRange`, [client](../../src/Jularr.Frontend/Framework/Components/SizeRange.entry.ts),
  [rules](../../src/Jularr.Frontend/Framework/Components/SizeRange.ts), `size-range.test.mjs`; the end of the track is no limit); no media-type bar
  and no waiting time or delay option anywhere; `ArrAdminTests`, E2E `ArrSettingsTests`) and one [profile
  editor](../../src/Jularr.Frontend/Pages/Admin/Profile/Profile.cshtml.cs)
  ([styles](../../src/Jularr.Frontend/Pages/Admin/Profile/Profile.entry.scss); the [mockup](../mockups/profile-scoring/SPEC.md)'s view 1: General with
  the media types of its quality list as a table of *Applies to* and *Default*, Qualities with round move arrows, Size, Languages, Release groups
  (good and bad group points, a link to the rating with its counts), Upgrades (minimum score, upgrade-until score, minimum increase) and a closed
  Advanced with the quality groups and the score matrix: every format with this profile's score, zero left out;
  `AcquisitionTests.Profiles_ReleaseGroupScores_*`).
  [AdminMenu](../../src/Jularr.Frontend/Framework/AdminMenu.cs) holds the one row of chips that joins all of them (the Indexers entry; Quality
  profiles with its panes is an entry of its own; Downloads, Servers, Speed and schedule and External clients are entries of their own group)
  ([DownloadPages](../../src/Jularr.Frontend/Framework/DownloadPages.cs) keeps the pane names and the setup wizard's own row),
  [ListToolbar](../../src/Jularr.Frontend/Framework/Components/ListToolbar.cs) puts it above a list with the list's own tools.
