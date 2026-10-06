# Profile / Activity — Clean Design

Status: **binding planning specification; approved Profile/Activity visual direction.**

This is the binding consumer specification for the signed-in user's own Profile and media Activity surface. Personal Devices/Sessions are owned by Settings > Devices & Sessions and are only linked from Profile.

It is not a social profile, analytics dashboard or Admin session monitor.

## Purpose

The Profile area should answer:

1. Which Profile am I using, and which Account owns the session?
2. What have I recently watched, read or listened to?
3. What am I currently consuming?
4. Where do I reach personal Settings, Devices & Sessions and account actions?
5. If authorized, where do I enter Admin without mixing Admin data into Profile?

Only the signed-in user's own personal state is shown.

## Information architecture

Profile is one coherent consumer area with shared profile chrome and tabs:

1. **Activity** — default
2. **Stats**
3. **Ratings**
4. **Friends**, only when a real Friends/social capability is implemented and available
5. **Settings**

The profile hero, mini stats and activity heatmap stay above the tabs.

Do not render a dead Friends tab. Until a canonical Friends capability/domain contract exists, Friends is hidden rather than backed by UI-only placeholder state.

Do not create many tiny account pages.

### Desktop / wide tablet

Use one shared Profile/Account page with the hero, mini stats, heatmap and tabs. The default Activity tab contains only the chronological Activity feed and its filters; Ratings, Friends, Stats and Settings remain in their own tabs.

### Mobile

Profile uses the same account tabs on Mobile in a compact form. Activity is the default and contains only the Activity feed. Devices/Sessions live under Settings > Devices & Sessions.

### Desktop Activity navigation

If Desktop retains a direct Activity destination, it must render the same canonical Activity surface/data as Profile Activity rather than creating a second history implementation.

## Canonical data contract

Use shared personal-state concepts:

- `Profile`
- canonical `MediaProgress`
- canonical consumption / playback / reader history
- ratings and profile presentation state where relevant

Devices, authentication sessions and `ActiveSession` are not duplicated into the Activity page. Their owning surfaces remain Settings > Devices & Sessions and playback/continuation UI.

Activity entries reference canonical Work plus optional Episode / Volume / Chapter targets.

No UI-specific AnimeHistory, MangaHistory, BookHistory or AudiobookHistory stores.

Admin Jobs/Operations history is unrelated and must never be mixed into personal Activity.

## 1. Profile header

Keep compact.

Show:

- avatar
- display/profile name
- account identifier/email only when useful
- profile switch action when multiple profiles are allowed
- Edit Profile where supported
- Settings shortcut

Optional role/restriction information appears only when useful for navigation. Do not show permission dumps, database IDs, server information or giant statistics.

### Account vs Profile

The authenticated Account and active Profile are distinct.

- Account owns authentication/security, linked login identities and account-level authorization.
- Profile owns personal media state/preferences.
- Account identifier/email appears only where genuinely useful and must not be confused with the Profile display name.

### Profile switching

If several Profiles are available, `Switch profile` opens the shared picker defined in `docs/mockups/login-profile-selection/SPEC.md`.

Switching keeps the Account session but changes all Profile-scoped progress, activity, ratings, preferences, Learning state and personal Connections. Histories are never merged between Profiles.

Desktop/Mobile may direct-start when exactly one Profile is usable. TV prioritizes the picker on shared screens.

## 2. Activity / History

Chronological personal media history. This is not an analytics feed and not a server event log.

One visual grammar handles Watched, Read, Listened, Completed and meaningful Resume activity.

Do not log every seek, pause or autosave as a visible Activity entry.

Each entry may show:

- small cover/poster
- Work title
- season/episode, volume/chapter or audiobook chapter context
- action/result
- date/time
- final/current progress where useful
- device only when useful
- Resume/Open when still resumable

Group chronologically: Today, Yesterday, This week, then older dates/months. Do not group primarily by media type.

### Filters

Keep compact: All, Watching, Reading, Listening, Completed. Date/search can live in a filter panel/sheet if needed. Do not create a chip wall.

### History actions

Where supported: open media, resume, remove one personal history item, or clear history through a Settings/privacy flow with confirmation.

Deleting visible history must not silently reset canonical progress. History deletion and progress reset are separate actions.

## 3. Ratings

Ratings are a first-class personal media feature and use one universal canonical rating model across all media types.

The Ratings tab shows the signed-in profile's ratings in a compact sortable/filterable list or grid.

Useful information:

- cover/poster
- Work title
- media type only where context requires it
- user's rating rendered in the user's selected rating system
- date rated / last changed
- optional short review/comment when that feature exists

Actions:

- change rating
- remove rating
- open Work
- filter/sort by media type, score and date

Do not create separate AnimeRating, MovieRating, MangaRating, BookRating or AudiobookRating stores.

### Rating display system

Each profile can choose how ratings are entered and displayed.

Supported presentation/input systems:

- **Three-level thumbs**: Thumbs Down / Thumbs Up / Double Thumbs Up
- **5 stars**
- **0–10 integer**
- **0.0–10.0 decimal**
- **0–100**

The selected system is a **profile preference**, not a database schema choice.

Changing the display system must never rewrite all stored ratings. A canonical normalized score is converted only for display/input.

For discrete systems such as thumbs:

- existing canonical values are bucketed for display;
- choosing a thumb state writes a defined canonical anchor value;
- `No rating` remains distinct from the lowest possible rating.

The exact visual control for each rating system belongs to shared components so Detail pages, Ratings tab and Activity use the same behavior.

### Rating visibility in Activity

Activity may show a rating only when the activity item genuinely includes a rating action/change.

Normal Watch/Read/Listen Activity rows must not automatically show rating badges.

## 4. Devices & Sessions handoff

Profile does not render a second device/session manager.

Use a compact link/row to `Settings -> Devices & Sessions` when useful. That page owns:
- current/recent devices;
- authentication/account sessions;
- sign out/revoke;
- device rename/inactive management.

Activity remains only personal media activity.

## 5. Personal links

Use compact navigation rows, not dashboard tiles:

- Settings
- Downloads / Offline when implemented
- Account & Security
- Admin only if authorized

Entering Admin changes to the Admin information architecture. Admin widgets never render inside Profile.

## Activity vs Progress

`MediaProgress` answers where the user currently is and what is completed. Activity/history answers what consumption happened over time.

Deleting Activity does not automatically reset Progress. Reset Progress is an explicit separate confirmed action.

## Visual style / Light / Dark

This page uses the global visual-style contract:
- Clean and Original Jularr share the same layout/components;
- Light/Dark/System are supported in both;
- accent uses shared semantic tokens.

Clean remains neutral and decoration-free. Original Jularr may apply its approved Japanese decorative skin where it does not compete with media/profile content.

## Desktop

Recommended first mockup:

- standard Jularr left sidebar + top global search
- approved profile hero/banner with avatar, nickname and Edit Profile
- mini stats directly under the hero
- activity heatmap
- tabs: Activity / Stats / Ratings / Friends / Settings
- Activity tab is a single wide chronological feed with compact filters
- no Current Session, Ratings summary or Friends summary inside the Activity tab

The page should look like a clean account/history surface, not an Admin dashboard.

## Mobile

Profile is a primary navigation destination.

Mobile keeps the shared profile hero in a compact form, mini stats, heatmap and account tabs. Activity is the default tab and uses chronological stacked rows with compact filtering. Devices/Sessions are managed from Settings rather than duplicated on Activity.

## Tablet

Portrait stays close to Mobile. Landscape may use the Desktop two-column layout. Touch remains primary.

## TV

TV Profile is deliberately limited to current profile, profile switching, recent/continue activity and TV-relevant Settings. Device/session administration hands off to Web/Mobile.

Account-security forms, dense history management, broad device administration and Admin hand off to Web/Mobile.

## Loading / Empty / Partial / Error

- Use skeletons for identity and several Activity rows.
- New profile: concise `No activity yet` plus Home/Discover destination.
- Zero-result filter: explain filters are hiding history and offer Reset.
- Section failures stay local; Profile identity should not block on optional history systems.
- Old history may remain visible even when media/storage is currently unavailable.

## Privacy / permissions

- only own Activity on this surface; Devices/Sessions follow their own Settings authorization
- same restriction enforced server-side
- Admin link only with capability
- hidden capabilities disappear rather than rendering disabled clutter

## Accessibility

Semantic headings, keyboard access, visible focus, accessible full timestamps, textual progress/activity state, touch-sized Mobile targets and predictable TV focus order.

## Navigation / back

Opening media from Activity and returning should preserve Activity filter, scroll position and source context where practical. Visible Back uses shared contextual navigation.

## Must not implement

- no social followers/friend profile system
- no XP/gamification dashboard
- no separate rating tables or scales per media type
- no storing a user's chosen visual rating scale as the canonical score itself
- no watch-time statistics wall
- no Admin operations/jobs/history
- no duplicate device/session manager inside Profile Activity
- no IP/codec/transcode/server-load details
- no parallel per-media history stores
- no giant dashboard tiles
- no separate Activity implementation for Desktop nav vs Profile
- no progress reset when merely deleting history
- no Admin widgets embedded in Profile
- no dense account/security forms on Profile overview
- no disabled forbidden actions shown as clutter

## Mockup deliverables

First review:

The existing Profile/Activity mockups in this folder are approved planning references. No dedicated Settings mockup is required; `user-settings/SPEC.md` defines the standard component/layout contract. Additional mockups are only needed later when implementation review finds a visual problem or a non-standard interaction needs approval.

Text specification wins over images on conflict.