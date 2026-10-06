# Anime / Series Detail — Clean Design

Status: **approved Desktop UX direction**.

Binding missing-media playback intent: `docs/mockups/instant-play/SPEC.md`.

## Purpose

This is the canonical user-facing detail page for episodic video works such as Anime and TV Series.

The page should feel cinematic at the top, but become practical immediately below the Hero for season and episode navigation.

Light and Dark are both first-class. Mobile, Tablet and TV variants must preserve the same information hierarchy while adapting interaction.

## Approved Desktop layout

### 1. Hero

Use one large backdrop across the full content width.

The Hero contains:

- title
- optional native/original title
- year
- media type
- season/structure summary
- a few important genres/themes only
- short description
- primary action is state-dependent: `Continue` / `Play` when the next required episode is local; `Start watching` when that episode is missing but Playback + instant acquisition are permitted; `Request` when explicit acquisition/approval is required; otherwise show the existing live Request state
- `Start watching` targets only the next required canonical episode and follows Looking for media -> Getting episode -> Preparing -> Starting playback; it does not implicitly request/download the whole Series
- manager-only instances never show Start watching/Player actions: missing content uses Request and acquired content ends at Available/Monitoring
- secondary personal-state action: Watchlist/Favorite where supported
- optional Collection action/overflow for less common actions

### Hero artwork

- **Do not show a second poster/cover card inside the Hero.**
- Use the backdrop as the visual focus.
- If no suitable backdrop exists, use the global Hero fallback system defined for Home: cover/poster + derived blur/gradient background.

### Hero information strip

The compact information that previously sat in a separate bar/card must live **inside the Hero**, horizontally near the lower area.

Examples:

- rating
- episode count
- studio
- source material
- available audio/subtitle language summary

Keep these compact and icon-led.

Do not create a detached full-width information bar underneath the Hero.

## 2. Main content layout

Desktop uses three functional areas:

### Left: Seasons

Persistent vertical season selector.

Each season entry shows:

- representative thumbnail
- season name
- episode count
- optional year
- selected state

Examples:

- Season 1
- Season 2
- OVA / Specials

The selected season has a clear accent border/background.

This left season list is the primary Desktop season navigation.

A compact selected-season label may appear beside the Episodes heading, but it must not become a confusing second independent selector.

### Center: Episodes

The main content area is the episode browser.

Top controls:

- Episodes heading
- selected season context
- Sort
- Grid / List view switch where useful

Episode card:

- episode thumbnail
- season/episode number
- title
- short description
- runtime
- progress when started
- compact language availability
- compact availability/request state
- overflow actions

The card must answer:

- what episode is this?
- where did I stop?
- can I watch it in my preferred language?
- is it available/requested?

Do not show release-group or technical file details on the normal user page.

### Right: Related Works

Compact Related Works panel.

Use canonical Work relations where possible:

- sequel / prequel
- movie
- OVA / special
- source Light Novel
- Manga
- adaptation
- closely related canonical works

This is not the same as generic recommendations.

Generic “More Like This” recommendations can appear lower on the page if useful.

## 3. Season model

Desktop baseline:

- vertical season list on the left
- selected season controls the episode grid/list
- OVA/Specials can be separate season-like structural groups when appropriate

Mobile/Tablet may replace the left rail with a dropdown/sheet because width is limited.

TV may use a horizontal season rail or remote-friendly selector.

Do not force one identical season picker across all platforms.

## 4. Language availability

Use the same semantic language system as Library/Discover.

For video, distinguish:

- preferred audio available
- preferred subtitles available
- only other languages available
- requested in preferred language
- requested in another language
- unavailable / not requested

Episode cards keep this compact.

Detailed track lists belong in episode/details/player surfaces.

## 5. Availability / request state

Per episode, support compact states such as:

- Available
- Requested
- Downloading
- Preparing
- Other language only
- Unavailable / not requested

The normal user page should not expose Sonarr-style candidate/release tables.

Requesting missing content should be possible from the episode or appropriate season/work action when the user has permission.

There is no consumer `Add` acquisition action on this page. Personal Watchlist/Favorite/Collection state is separate from Request.

## 6. Related vs recommendations

Keep these concepts separate:

- **Related Works** = canonical franchise/adaptation relationship
- **More Like This** = recommendation/similarity result

Related Works is more important on this page and should stay visible without scrolling too far.

## 7. Visual rules

- clean Jularr design system
- cinematic Hero, functional content below
- no unnecessary standalone statistics row below Hero
- no second poster inside Hero
- no tag clutter
- compact accent usage
- consistent card radii/spacing with Library and Discover
- Light and Dark both intentionally designed

## 8. Responsive direction

### Mobile

- Hero becomes shorter and touch-friendly
- season navigation becomes dropdown / sheet / horizontal compact selector
- episode cards become stacked rows/cards
- actions use sheets/dialogs where needed
- language and availability remain compact

### Tablet

- may retain a narrow season rail in landscape
- portrait can use mobile-style season selector
- larger touch targets

### TV

- remote-first
- season rail/row with clear focus state
- episode cards have strong selected focus treatment
- selected episode can reveal additional progress/language info
- Back preserves selected season and episode focus position

## 9. States

Must support:

- loading
- missing Hero artwork
- no local episodes yet
- partial season availability
- requested/downloading episodes
- unavailable preferred language
- provider metadata partially unavailable
- storage offline while metadata remains browsable
- error

## 10. Reference image

Recommended approved reference filename:

- `anime-series-detail-clean-approved.png`

Store it in:

- `docs/mockups/anime-series-detail/`

The approved image defines visual direction; this specification is authoritative for behavior and information hierarchy.

## Implementation rule

Agents must not:

- reintroduce a poster card into the Hero
- move Hero stats into a detached dashboard-like bar
- replace the Desktop left season selector with random chips
- expose Admin/release internals on the user detail page
- redesign the page without updating this spec and approved mockup
