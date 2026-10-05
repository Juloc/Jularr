# Movie Detail — Clean Design

Status: **approved UX direction**.

Binding missing-media playback intent: `docs/mockups/instant-play/SPEC.md`.

## Purpose

Movie Detail is the canonical user-facing page for a single movie.

The page should feel cinematic at the top, then expose useful movie content in one natural scroll flow.

Light and Dark are both first-class. Mobile, Tablet and TV keep the same information hierarchy with platform-specific interaction.

## Approved overall structure

1. Hero
2. Trailer / preview
3. compact Versions & Languages
4. Cast & Crew
5. Specials / Bonus Content
6. Related Works
7. More Like This
8. About / Details

This is one continuous page.

## Important: do NOT copy the tabs from the visual reference

The generated reference image contains tabs such as:

- Overview
- Trailer
- Cast
- Specials
- Related

These tabs are **not approved** and must **not be implemented**.

They are an artifact of the concept image only.

The final Jularr Movie Detail page is a **single scrollable page** where the sections are directly visible one after another.

Do not hide core movie content behind tabs.

A compact jump-to/section navigation may be considered later only if the page becomes unusually long, but it must not replace the direct scroll structure.

## 1. Hero

Use one large cinematic backdrop.

Hero content:

- title
- optional original/native title
- year
- Movie
- runtime
- a few useful genres
- short description
- primary action is state-dependent: `Play` / `Continue` when a usable version is available; `Request` when unavailable and requestable; otherwise show the existing live Request state
- Watchlist/Favorite where supported
- optional Collection/overflow for secondary actions

### Hero information strip

Keep compact metadata **inside the lower Hero area**, horizontally:

- rating(s)
- studio / distributor where useful
- source where relevant
- high-level format/capability where user-relevant
- compact Audio / Subtitle language summary

Do not add a detached dashboard/statistics strip below the Hero.

Do not add a second poster inside the Hero unless a later approved layout explicitly requires it.

## 2. Trailer / Preview

Immediately below the Hero, provide a prominent trailer/preview area when trailer metadata exists.

Can contain:

- official trailer
- teaser
- additional trailer
- Behind the Scenes preview where appropriate

Primary trailer gets the large preview.

If no trailer exists:

- omit the section or
- use a backdrop preview only when useful

Trailer availability must never be required for the page to feel complete.

## 3. Versions & Languages

This should be **compact**, not the main content block.

The normal page shows a concise summary:

- Audio languages
- Subtitle languages
- relevant user-facing capabilities such as 4K / HDR / Dolby Vision / Dolby Atmos when available

Provide a `View all` / details action for the full version/track view.

The detailed view can show:

- available versions
- original vs dubbed versions
- audio tracks
- subtitle tracks
- preferred-language availability
- requested language/version
- quality/format variants

Do not expose release-group/import/file internals on the normal user page.

There is no consumer `Add to Library` acquisition action on Movie Detail. Explicit acquisition uses the shared Request flow; Watchlist/Favorite/Collection are separate personal-state actions. When Playback is enabled and instant acquisition is permitted, a missing Movie may expose `Watch now` as a playback intent: it transparently creates/reuses the canonical Request and progresses through Looking for media -> Getting movie -> Preparing -> Starting playback. On manager-only instances the missing Movie shows Request and successful acquisition ends at Available.

## 4. Cast & Crew

Show a horizontally scrollable cast/crew row.

Each person card:

- portrait
- actor/person name
- role / character

Examples:

- lead cast
- supporting cast
- director
- key crew where useful

For animated movies, voice cast can be prioritized.

Selecting a person may open a person/detail/discovery view later.

## 5. Specials / Bonus Content

Only show when data/content exists.

Examples:

- Behind the Scenes
- Featurettes
- Deleted Scenes
- Interviews
- Making-of content
- Short bonus videos
- associated special/OVA where appropriate

Each item:

- thumbnail
- title
- runtime
- content type

If no Specials exist, omit the entire section. Never show an empty filler block.

## 6. Related Works

Use canonical relations, not generic similarity.

Examples:

- previous movie
- sequel
- prequel
- source novel / manga / light novel
- spin-off
- related series
- adaptation

Keep relation type visible but compact.

## 7. More Like This

Recommendation row for similar titles.

This is separate from Related Works.

Use simple Discover-style cards.

## 8. About / Details

Lower-page informational section.

Can contain:

- full synopsis
- director
- studio
- distributor
- release date(s)
- runtime
- certification / age rating
- genres/themes
- original title
- production country where useful
- source material

Do not duplicate every value already visible in the Hero unless more detail is added here.

## Language / request behavior

Use the shared Jularr language semantics.

Movie page should make it obvious whether:

- preferred audio is available
- preferred subtitles are available
- only other language(s) are available
- preferred language has been requested
- another language has been requested
- content/version is not available

Detailed tracks belong in the Versions & Languages detail view.

Audio/subtitle Track choices are not fake Editions. A distinct video Edition exists only for a materially distinct presentation/cut/publication; ordinary dub/subtitle selection remains Track selection and Player behavior.

## Responsive behavior

### Desktop

- wide Hero
- Trailer can be large
- Cast, Specials, Related and Similar use horizontal rows
- Versions & Languages may sit beside Trailer/Synopsis as a compact card when space allows

### Mobile

- Hero becomes vertical/touch-first
- no desktop tab bar
- sections stack naturally
- Trailer remains prominent
- Versions & Languages becomes a compact card with drill-in
- Cast / Specials / Related / Similar become horizontal swipe rows
- actions use large touch targets
- sticky Play/Continue action may be used if it does not obstruct content

### Tablet

- adaptive two-column blocks where space allows
- touch-first
- otherwise same hierarchy as Desktop

### TV

- cinematic Hero
- remote-first focus
- horizontal content rails for Trailer, Cast, Specials, Related and Similar
- Versions & Languages opens a remote-friendly overlay/details surface
- focus position is preserved when returning

## States

Must support:

- loading
- no backdrop
- no trailer
- no cast metadata
- no specials
- no related works
- only other-language version available
- requested language/version
- storage offline
- provider metadata partial
- error

Sections with no meaningful content should usually disappear rather than render empty placeholders.

## Visual reference

Recommended approved image filename:

- `movie-detail-clean-approved.png`

Store under:

- `docs/mockups/movie-detail/`

The image is a visual reference only.

**The image's tab navigation is explicitly non-authoritative and must not be copied.**

This specification is authoritative for structure and interaction.

## Implementation rule

Agents must not:

- implement the tabs visible in the concept image
- hide Trailer/Cast/Specials/Related behind tab navigation
- make Versions & Languages the dominant page content
- expose Admin/release internals
- add a second unrelated information dashboard under the Hero
- redesign the page without updating this spec and approved reference
