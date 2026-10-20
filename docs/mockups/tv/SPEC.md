# Android TV layout

Status: **binding planning specification for the TV surface.** The TV shows the desktop (web) layout of the clean-start frontend, at TV scale, with the focus instead of the pointer.

The quick view on the media-preview surface, the watchlist card and the shared tokens are described in the documents they belong to; this file fixes the TV layout, the focus model and what the TV takes from the previous client.

## 1. The decision

**The TV is the desktop, at TV scale.** Same navigation, same top row, same hero, same shelves, same cards, same colors and the same words. Three differences come from the platform and nothing else:

- the TV is read from a distance, so everything is bigger (one scale factor, see 3);
- the pointer becomes the D-pad, so the focus takes over what the hover did;
- there is no mouse, so the controls that only work with a mouse are not offered.

**The accent is the desktop's accent** (`--accent`, the Clean skin's `#7c3aed` with its light/dark text and hover variants), not the amber of the previous TV client. The TV reads the tokens from the same source as the web.

## 2. What the TV takes from the previous client

The previous TV client (`feat/android-tv-integrated-906-20261009`, the #906 mockup parity) is **not** rewritten; it is ported. Its behavior is worth keeping and its look is not:

| Part | Origin | Work |
| --- | --- | --- |
| Player (screen, chrome, pure interaction, seek scenes, trickplay) | previous client | port the code, adopt the shared tokens |
| Focus system (focus memory per screen and account, focus style, D-pad semantics) | previous client | port unchanged |
| Navigation, session and account handling | previous client | port, adapt the device token |
| Client API layer (`core-api`, `core-model`) | previous client | **rewrite** for `/api/v1`, the device token, `workId`/`mediaAssetId` as `long` and the canonical card |
| Offline downloads, TTS bridge, update check, shared tokens | previous client | port |
| The screens (Home, Watchlist, details, search, settings, profile) | previous client | **rebuild in the desktop look** (the colors are hardcoded per screen there, so the look cannot be flipped by a token) |

The result is one client: the behavior of the previous client, the look and the data of the desktop.

## 3. The scale

One factor for the whole TV, applied to the desktop values. The cards and the hero are 340px (the Owner's decision, 20 October 2026):

| Desktop | TV | Source |
| --- | --- | --- |
| body 14px | 20px | `shell.entry.scss` |
| card 230px wide, radius 24px, 2:3 | 340px wide, radius 32px, 2:3 | `MediaCard.scss` |
| card title 14/19, two lines | 20/26, two lines | `MediaCard.scss` |
| card meta 12px | 17px | `MediaCard.scss` |
| shelf heading 20px/700 | 29px/700 | `MediaShelf.scss` |
| hero title 28px | 41px | `MediaHero.scss` |
| hero min-height 260px, radius 16px | 340px, radius 20px | `MediaHero.scss` |
| top row min-height 64px | 92px | `Shell.scss` |
| side rail item min-height 44px | 64px | `Shell.scss` |
| availability symbol 32px | 46px | `MediaAvailability.scss` |

The spacing scale (the gaps between the shelves, the cards and the sections) grows with the same factor.

## 4. The layout map

| Desktop | TV |
| --- | --- |
| Left side rail (brand, Home, Discover, Watchlist, Calendar, Library; below: Unfinished, Admin, Account) | **Home, Watchlist, Library** and below them the **profile switcher** and the **app settings** (the server address, the appearance, the voice and the options of the profile that matter for the TV). The rail is always visible and never collapsible. Discover, Calendar, Unfinished, Admin and Account are not on the TV. |
| Top row (breadcrumb, global search, theme, notifications, profile) | The same row: the current page as the breadcrumb, the search button, the profile. The theme switch stays in the app settings, as on the desktop. |
| Media-type tabs (the NavigationTabs capsule: All, Watch, Filme, Serien, Anime, Read, Bücher, Manga, Light Novels, Hear, Musik, Hörbücher, Game, Games) | The same capsule, one row, horizontally scrollable; the D-pad moves inside it. Only the scopes the instance and the profile allow. |
| Hero (backdrop, type + progress line, title, overview, "Fortsetzen"/"Details", the slide dots) | The same hero, 340px high. The D-pad moves the slides; OK on the hero opens the title's detail or starts the action. |
| Genre chips under the hero (Alles, Trending, Neu, Beliebt, Bald verfügbar, Fortsetzen) | The same chips, focusable. |
| Shelves (Weitermachen with its note, then the rows; the heading with the count and the chevrons) | The same shelves. The chevrons become the left/right of a focused row; the count stays as a number. The grid/list switch is not offered. |
| Card (portrait cover, availability badge top-left, bookmark top-right, title, meta, progress at the bottom, the actions on hover) | The same card, portrait 2:3. The focused card shows the actions: Play/Continue/Read/Request on the left, Details on the right, in equal-width columns, exactly as the web shows them on hover. The progress bar keeps its place at the bottom edge. |
| Toast / inline errors | The same, above the bottom edge. |

### 4.1 The search

The search opens from the **search button** in the top row (the magnifier of the desktop's search field) and works like the familiar streaming apps:

- OK on the search button opens the search screen;
- the **field** sits at the top; while typing, the **suggestions** pop up under it;
- a **virtual keyboard** for the D-pad stands on the **left and on the right** of the screen, so the letters are reachable with the D-pad from both sides;
- **under the keyboard** the **completions** (the matching titles) are listed and can be opened with OK;
- the search screen has no other chrome; Back returns to the screen it came from and restores the focus.

The keyboard is the TV's own: no system keyboard, no cursor, no text selection.

## 5. The focus model

- The focus ring is the desktop's `--focus` (the accent text color), 3px, with a slight scale of the focused card (1.04) and no glow.
- The D-pad moves in the reading order: the rail → the top row → the tabs → the hero → the chips → the shelves (up/down between the rows, left/right inside a row).
- OK activates the focused control; on a card it opens the title's detail page, on a shelf heading it opens the row.
- Back closes the search overlay, then a detail page, then the current screen; the focus returns to the control it came from.
- The focused card reveals its actions, the focused shelf heading shows its chevrons.
- Nothing is reachable only with the pointer, and no control is shown that does nothing.

## 6. What the TV does not get

- The hover trailer preview and the pointer-only controls of the web.
- The grid/list switch of a shelf.
- The notifications bell: not on the TV and not in the app settings.
- Discover, Calendar, Unfinished, Admin and Account: the rail carries Home, Watchlist, Library, the profile switcher and the app settings.
- The admin areas for a device that may not administer them.

## 7. The decisions of the Owner (20 October 2026)

- The cards and the hero are 340px, portrait 2:3 like the desktop.
- The rail carries Home, Watchlist and Library, plus the profile switcher and the app settings with what matters for the TV.
- The search opens from the search button and works like the familiar streaming apps: the field with popping suggestions, the D-pad keyboard on the left and on the right, the completions under the keyboard.
- The hero is 340px high.
- No notifications bell, not even in the settings.
- The accent is the desktop's accent, not the amber of the previous TV client.
- The previous TV client is ported, not rewritten: the player, the focus system, the navigation, the sessions, the offline engine, the TTS bridge and the shared tokens come over; the client API layer is rewritten and the screens are rebuilt in the desktop look.

## 8. The packages

1. **The client foundation** (done, #1301): the Gradle build, `core-model`, `core-api` (pairing, capabilities) and the Android CI.
2. **The app contract** (done, #1323): the canonical card, the watchlist, the instance presentation and the texts in `core-model`/`core-api`.
3. **The shared tokens** (done, #1324): the one file with the colors of a skin, the radii and the type scale; the web builds its stylesheet from it and the instance serves it to the clients.
4. **The TV shell** (done, #1328): the rail, the top row, the profile, the app settings, the type capsule and the shelves of the profile in the desktop's card.
5. **The TV player and the rest**: the previous client's player, the details page, the search with the D-pad keyboard and the offline engine, rebuilt on the new contract and in the desktop's look.
6. **The phone shell**: the WebView shell and the native player from the previous client, on the new contract.
