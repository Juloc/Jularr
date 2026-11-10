# Media cards, shelves and the media-type bar

The shared MediaCard, hover preview, availability, shelves and the media-type bar.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- Continue rows: every card of Continue watching, listening and reading has a three-dot menu (Plex-like) top left of the cover, always visible,
  finger-sized on touch: Remove from Continue (progress is kept; the title returns with its next progress), Mark watched/unwatched, favorites, Open
  details.
  Nothing admin-specific, no confirmation or undo (as the rest of the app).
- The media-type bar is also the type filter of the administration's Requests list (it narrows the list and its tab counts), the library overview, the
  Naming panels and the AniList matches, the Wanted list (its view counts follow it, and its Search all searches the chosen types' targets)
  (`?type=a,b`) and the Media import preview.
  There the bar lists the types the list holds and narrows without reading the list again: rows of a type left out are hidden and not chosen (they are
  not imported, and Select all and the count follow the rows shown); after Update preview they stay unchosen.
- Library and Favorites share the same media-type bar, filter panel (types, release year), order and layout.
  The admin Media list has the same media-type bar above its toolbar (every type, several at once); its status chips stay in the toolbar and its
  filter drawer has no type.
  The same media-type bar is on every page where a media type is chosen to narrow a list: also My requests (below the status tabs) and the Calendar
  (replacing its own chip bar); the Anime tab appears when Anime is active.
- All catalog/Continue cards use the same canonical `MediaCard` (the UiDesigner MediaCard, owner decision 20 Oct): a small portrait cover of 133-150
  px (a card to continue 176 px) with softly rounded corners, local availability top left as a tag, the heart top right, then the linked title and one
  line of facts: `year · type · rating` (what is unknown is left out; the rating is the catalog's, on a scale of ten).
  A card to continue reads `Type Year | detail` and `ContinueDetail` writes the detail (`S01 E05 · 22 min left`, `Chapter 183`,
  `Track 4 · 3 min left`, `42%`).
  Every text is one line with an ellipsis; a clipped text shows in full in a tip over the cover on hover, focus and tap.
  Hover or focus shows only the one contextual action over the cover, icon and short label (Request, Watch, Continue, Read), never Details; touch
  keeps it visible.
  A missing real primary action shows none; do not fabricate playback or requests.
  Series lifecycle, genres, descriptions and language capsules belong in Details, not the cover.
  Grids hold as many covers per row as fit (`media-grid`), no two-row wrapping of card text.
- Progress is a thin capsule inside the foot of the cover (translucent white track, accent fill); no time text and it never moves other content.
  A page may add one short label over the cover, bottom left (Favorites shows where the profile stands).
- Availability is green only when all monitored units have local profile-language audio; yellow means complete in other audio languages; orange means
  partial with profile audio; yellow–orange means partial in other audio languages.
  Subtitles do not change this classification.
  Purple downloading, blue searching/ queued, gray requested and no dot for missing stay distinct.
  Hover expands the dot into a compact tag (icon and a short word or the unit counts) with the full sentence below it.
  Library, Favorites, Collections, People, Franchise and Home read the whole projection; Discover cards carry the coarse state of the Work and show a
  tag only for a held or requested title.
  Backend must supply these facts.
  Existing list services do not yet expose audio completeness: production does not infer it from `MediaStateType` or episode counts.
- The card's action (owner's decision, 2 Nov) is one round icon on the cover, shown with the mouse's hover and always on a touch screen (a moving
  mouse, marked as `data-pointer` on the root, counts even where the browser reports no hover).
  Request ([CardRequest](../../src/Jularr.Frontend/Framework/Components/CardRequest.cs)) asks at once with the profile's language and the default
  quality (`/Details/{id}?handler=QuickRequest`); the icon then follows the live phase on the accent: plus, a clock while it waits for approval, a
  pulsing search, the download's percent in a ring, then a white play.
  While a request runs the icon stays without the hover, the availability tag follows the phase (from one set of tag templates per page), and the icon
  leads to the title's page, which shows the progress.
- The delayed hover preview (owner's hover-card mockup, 21 Oct; delay raised to 2 s on 2 Nov): after 2 s of the mouse resting on a cover, never on one
  of its buttons (or of the keyboard focus on it), the card opens a panel over the row, as high as the card and 1.84 times as wide, fetched then from
  `/MediaPreview` (nothing loads before, nothing by hover on touch or devices without hover).
  The cards of the row never change width or position: the card under the panel is only hidden, and the panel shifts sideways to stay inside the row
  and the viewport.
  Trailer mode: the title's trailer in a 16:9 frame (the one allowed external embed, the privacy host, one muted embed as high as the frame and three
  times as wide, so the provider's corners stay outside; Jularr's own pause top left, sound top right, a progress line and `0:12 / 2:24` from the
  player's own messages, a running line when it reports none) above the linked title, the year, type and runtime, and two lines of description;
  description mode, for a title without a trailer: a larger title, the facts and the longer description.
  The foot holds the card's action (a larger pill with its text; a request names its live phase), its heart and Details.
  A touch device has no hover: a card that stays in view near the middle for 2 s opens the same panel by itself, one at a time, but only a card of a
  shelf, never of a grid (the panel would cover the neighbouring covers and titles of the two columns of a phone), unless the profile switched
  `AutoplayPreviews` off (rendered as `data-autoplay-previews` on `#app-content`; data saver and offline also keep it off).
  It closes on leave, Escape, scrolling, resizing, a click elsewhere, a hidden document or page navigation, and drops its frame.
  The cached component module owns the delegated handlers once across app-shell visits.
  List contracts carry no trailer IDs: the page asks one Work at a time (its trailer is its `Trailer` fact; a catalog title without details shows its
  catalog entry's description alone, no provider is asked).
- Bookmark mutations use the existing Details POST, typed Form, antiforgery and Backend watchlist service (the page is called Favorites).
  Return only to validated local URLs.
  If a list does not supply current membership, the icon links to management in Details, not a false toggle.
  Every Discover card is a Work and carries the bookmark.
  Shelves retain native touch/keyboard scrolling and local chevrons, no visible scrollbar or navigation-only server reads.

## Frontend

- [MediaCard](../../src/Jularr.Frontend/Framework/Components/MediaCard.cs), [client](../../src/Jularr.Frontend/Framework/Components/MediaCard.ts): the
  UiDesigner cover for every catalog: small cover, availability tag, heart, one round hover action (icon only; its text shows in the hover preview),
  thin progress capsule, linked title and a line `year · type · rating` (`For` builds it from a library item or a Discover item, `Resume` for a card
  to continue, [ContinueDetail](../../src/Jularr.Frontend/Framework/ContinueDetail.cs) writes its detail); clipped text shows in a tip.
  [CardRequest](../../src/Jularr.Frontend/Framework/Components/CardRequest.cs) requests at once (`QuickRequest` handler of Details) and follows the
  live phase of the request, the tag top left with it; `DiscoverTests.CardRequest_*`, E2E `Steps.RequestAsync`.
  [MediaBookmark](../../src/Jularr.Frontend/Framework/Components/MediaBookmark.cs) uses the existing Details POST with a local return URL and
  antiforgery; missing membership links to Details.
  [Client tests](../../src/Jularr.Frontend/Tools/media-card.test.mjs).
- [MediaPreview](../../src/Jularr.Frontend/Framework/Components/MediaPreview.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/MediaPreview.ts), [tests](../../src/Jularr.Frontend/Tools/media-preview.test.mjs): opt-in
  status links, inert SSR content and one native modal; official trailer embed on Play, released on close.
  Design examples await real trailer/request/download service fields.
- Three dots of a continue card ([ContinueMenu.cs](../../src/Jularr.Frontend/Framework/Components/ContinueMenu.cs) on `MediaCard.Menu`,
  [CardMenu.ts](../../src/Jularr.Frontend/Framework/Components/CardMenu.ts), started by `MediaCard.ts`, styles in
  [MediaCard.scss](../../src/Jularr.Frontend/Framework/Components/MediaCard.scss), [tests](../../src/Jularr.Frontend/Tools/card-menu.test.mjs), E2E
  `ContinueMenuTests`, `ProgressTests.Home_ContinueCard_*`): the home page's three continue rows (watching, listening, reading) put a round button top
  left of the cover with the shared [ActionMenu](../../src/Jularr.Frontend/Framework/Components/ActionMenu.cs) panel: Remove from Continue, Mark
  watched/read/heard and unwatched (the episode or volume the profile stands at), add to or remove from favorites, Open details.
  Every entry is a form of the title's page (`/Details/{id}` with its `Progress` handler, the favorites post and `HideContinue`), so it works without
  script; with script `CardMenu.ts` sends it with `X-Requested-With: fetch` (the handlers answer 204), drops the card (and an empty row) at once or
  flips the favorites text; the panel is fixed to the screen at the button, right of the sidebar, with arrow keys and Escape.
  Removing keeps the progress (`MediaProgress.ContinueHiddenAt`, migration 0055; the next progress write brings the title back).
- Delayed hover over a cover ([CardPreview.ts](../../src/Jularr.Frontend/Framework/Components/CardPreview.ts), started by `MediaCard.ts` on every page
  with cards, [tests](../../src/Jularr.Frontend/Tools/card-preview.test.mjs), `HoverPreviewTests`): after 2 s of the mouse resting on the cover (never
  on a button of the card; the keyboard focus counts the same; on a touch device a card that lingers in view for 2 s starts it unless the profile's
  `AutoplayPreviews` is off, read from `data-autoplay-previews` on `#app-content`) the card fetches `data-preview-src`
  ([MediaPreviewAddress](../../src/Jularr.Frontend/Framework/MediaPreviewAddress.cs)) from the [MediaPreview
  page](../../src/Jularr.Frontend/Pages/User/MediaPreview/MediaPreview.cshtml.cs), a fragment of
  [MediaCardPreview](../../src/Jularr.Frontend/Framework/Components/MediaCardPreview.cs), and opens it as a popover over the row, as high as the card
  and 1.84 times as wide, its box from the card and row (`previewBox`,
  [MediaCard.scss](../../src/Jularr.Frontend/Framework/Components/MediaCard.scss); the card under it is hidden, never moved): the muted trailer in a
  16:9 frame (the embed as high as the frame and three times as wide) with its own pause and sound controls and the player's real progress (the one
  allowed external embed, `youtube-nocookie.com`, loaded only then and dropped on leave, Escape, scrolling, resizing and a hidden page), or the larger
  title and description for a title without a trailer; the card's own action (a pill with its text) and heart move into its foot beside Details.
- [MediaAvailability](../../src/Jularr.Frontend/Framework/Components/MediaAvailability.cs): dot-to-capsule status with shared vivid palette, partial
  counts and explicit profile/other-audio combinations.
  The state comes from the Backend's canonical `MediaAvailabilityState` (the library and the watchlist supply it with the work card); the Frontend
  infers nothing.
  Trailer IDs are still missing from the list contracts.
  Eight development samples: `/Design?pane=cards`.
- [MediaLanguages](../../src/Jularr.Frontend/Framework/Components/MediaLanguages.cs),
  [IconInfo](../../src/Jularr.Frontend/Framework/Components/IconInfo.cs): supplied preferred flags/counts and native accessible disclosures; Design
  labels missing service fields.
- [MediaShelf client](../../src/Jularr.Frontend/Framework/Components/MediaShelf.ts): shared local chevrons, native touch/keyboard scrolling and
  fragment observer cleanup; [tests](../../src/Jularr.Frontend/Tools/media-shelf.test.mjs).
- [MediaTypeBar](../../src/Jularr.Frontend/Framework/Components/MediaTypeBar.cs) is the one media-type row of Discover, Library, Favorites, My
  requests, Calendar, the admin Works list, Wanted, the AniList matches and the Media import preview (over
  [MediaAreas](../../src/Jularr.Frontend/Framework/MediaAreas.cs) and `CapsuleBar`;
  [ScopeSelection](../../src/Jularr.Frontend/Framework/LibraryScopes.cs) reads the types of an address against the profile's),
  [MediaFilterDrawer](../../src/Jularr.Frontend/Framework/Components/MediaFilterDrawer.cs) the filter panel of Library and Favorites (types by area,
  release year) and [LayoutToggle](../../src/Jularr.Frontend/Framework/Components/LayoutToggle.cs) (device layout grid or rows,
  [client](../../src/Jularr.Frontend/Framework/Components/LayoutToggle.entry.ts)) their layout switch.
- [CapsuleBar](../../src/Jularr.Frontend/Framework/Components/CapsuleBar.cs): all capsule plus grouped capsules whose links carry the whole selection;
  [HeroCarousel](../../src/Jularr.Frontend/Framework/Components/HeroCarousel.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/HeroCarousel.ts): one highlight at a time with arrows, progress bar and pause, the shown
  ones chosen from device preferences ([tests](../../src/Jularr.Frontend/Tools/hero.test.mjs));
  [Drawer](../../src/Jularr.Frontend/Framework/Components/Drawer.cs) (native modal panel, optional GET form),
  [ChoiceGroup](../../src/Jularr.Frontend/Framework/Components/ChoiceGroup.cs) (radio/checkbox pills),
  [ChoiceSelect](../../src/Jularr.Frontend/Framework/Components/ChoiceSelect.cs) (GET select that submits on change).
- [MediaHero](../../src/Jularr.Frontend/Framework/Components/MediaHero.cs),
  [MediaShelf](../../src/Jularr.Frontend/Framework/Components/MediaShelf.cs),
  [MediaImage](../../src/Jularr.Frontend/Framework/Components/MediaImage.cs): shared media presentation; Backend owns image processing.
  `MediaImage` is the one place provider images are rewritten: a stored cover gets its width variants as `srcset`, every other provider image goes
  through the `/api/v1/covers/external` cache (never loaded from the provider by the browser; only trailer thumbnails stay on YouTube).
  See Covers in the [CODEMAP](../CODEMAP.md).
- Cards declare `sizes="auto, 176px"` so lazy images use their rendered width with the existing cover variants; external covers stay fresh in the
  browser for seven days.
  Browser verification: [CoverDeliveryTests](../../tests/Jularr.E2E.Tests/CoverDeliveryTests.cs).
