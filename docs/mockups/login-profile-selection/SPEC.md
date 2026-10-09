# Login / Profile Selection

Status: **approved UX direction; binding planning specification**.

This specification defines Jularr account sign-in, external login identities, profile selection, profile switching and the two supported visual styles for the unauthenticated entry surface.

It does not replace User Settings > Connections. Authentication identity and media-service synchronization are separate concerns even when the same external service supports both.

## 1. Core model

Jularr distinguishes:

- **Account** — authentication, security, roles/capabilities and account-level sessions.
- **Profile** — personal media state and presentation context used after sign-in.
- **Login identity** — a credential/provider identity that authenticates one Jularr Account.
- **Connection** — an optional external service link used for sync/import/write-back capabilities such as watch state, ratings or lists.

One Account may own one or more Profiles when instance policy allows it.

Login establishes an Account session. Profile selection then chooses the active Profile for that session/device.

Canonical flow:

```text
Login -> Profile selection when needed -> Jularr
```

If the Account has exactly one usable Profile, Jularr may enter it directly.

Switching Profile never logs out the Account. Logout ends the Account session.

## 2. Profile-scoped state

The active Profile owns or scopes personal state including:

- MediaProgress;
- playback/reading/listening history;
- ratings;
- watchlist/list membership where personal;
- personal Collections where supported;
- language/media preferences;
- playback/reader preferences;
- Learning state;
- personal provider Connections;
- personal notifications/settings where applicable.

Changing Profile must never merge, copy or leak these states implicitly.

Account-level security, login identities, roles and capabilities remain Account concerns.

## 3. Login screen — information hierarchy

The Login page is deliberately minimal.

Show:

1. Jularr logo/instance branding;
2. username or email field;
3. password field when password authentication is enabled;
4. `Stay signed in` / remember-session control when supported;
5. primary `Sign in` action;
6. configured external login providers;
7. Passkey action when supported;
8. recovery/self-registration actions only when those features are enabled.

Do **not** show:
- `Welcome back`;
- introductory marketing/help copy;
- descriptions such as `Sign in to access your media...`;
- feature lists;
- media cards;
- dashboards;
- decorative text that does not help authentication.

The logo and controls are sufficient context.

## 4. Password login

Fields:

- `Username or email`;
- `Password`;
- password visibility toggle.

Primary action:
- `Sign in`.

Optional:
- `Stay signed in`;
- `Forgot password?` only when a recovery flow actually exists;
- registration only when instance policy permits self-registration.

Do not show dead links.

Validation stays local and concise.

Authentication errors must not disclose whether a specific account exists when that would weaken security.

## 5. External login providers

Jularr owns the internal Account ID. External providers are linked login identities.

The Login page can show configured providers such as:

- Plex;
- Jellyfin;
- Trakt;
- AniList;
- MyAnimeList;
- Google;
- future provider adapters that explicitly declare login/identity capability.

Only providers that are:

1. implemented;
2. configured by the instance Admin;
3. enabled for login;
4. healthy enough to attempt authentication;

appear on the Login page.

Do not hard-code every integration into the page.

Provider buttons use recognizable icon + provider name. Keep the grid compact.

When many login providers are configured, show a bounded first set plus a compact `More sign-in options` disclosure/sheet rather than growing the Login card indefinitely. Ordering is instance-configurable or stable by provider priority.

A provider may support login, synchronization, both or neither. Login capability and Connection/sync capability are declared separately.

## 6. External identity behavior

External authentication resolves to a local Jularr Account.

Rules:

- provider user ID is never the Jularr Account ID;
- one Jularr Account may link multiple login identities;
- the same external identity cannot silently attach to two Accounts;
- linking an additional identity to an existing Account requires authenticated confirmation;
- provider account renames do not create a new Jularr Account;
- provider outages must not corrupt/remove the local Account;
- provider tokens/secrets remain server-side and are never exposed as normal UI state.

If instance policy permits automatic account creation from an external provider, the new Account receives conservative default rights. It must not gain Owner/Admin/Instant acquisition rights simply because an external login succeeded.

Plex login may be one such auto-provisioning flow when enabled by the Admin.

## 7. Login provider vs personal Connection

These are separate even for the same provider.

Example:

- `Sign in with AniList` authenticates an Account when AniList login is enabled.
- User Settings > Connections > AniList controls progress/rating/list synchronization for the active Profile.

Signing in through a provider does not automatically enable every sync capability unless the user/admin policy explicitly says so.

Disconnecting a personal synchronization Connection must not accidentally delete the Account's only login identity.

Removing a login identity must require another valid account authentication method or explicit safe account-recovery handling.

## 8. Passkeys

Passkey/sign-in-key support appears only when implemented and available for the account/browser/device.

Passkeys authenticate the Account, not an individual Profile.

Profile PIN remains a separate profile-selection guard and is not a substitute for account authentication.

## 9. Profile selection

Profile selection appears when:

- multiple usable Profiles exist;
- explicit instance/device policy requires a picker;
- the user chooses `Switch profile`.

Show a simple centered grid/list of Profiles.

Each Profile item shows:

- avatar;
- profile/display name;
- lock indicator only when PIN protected.

Do not show:
- email;
- account ID;
- roles;
- capability dumps;
- media statistics;
- recent activity;
- provider identities.

Selecting an unprotected Profile enters Jularr immediately.

## 10. Single-profile direct start

If exactly one usable Profile exists, Desktop/Mobile may enter Jularr directly after successful account authentication.

The active Profile may be remembered per authenticated device/session.

Remembering a Profile is device/session scoped. It must not change the default Profile on every other device.

TV may prefer the Profile picker more often because it is commonly a shared-screen device.

## 11. PIN-protected Profile

A Profile may optionally require a PIN before activation.

PIN screen shows only:

- Profile avatar/name;
- PIN input/keypad;
- Back.

No media background or unnecessary explanatory copy is required.

Security requirements:

- server-side verification;
- rate limiting / retry delay;
- no PIN in logs;
- no plaintext PIN persistence;
- PIN protects profile switching only and does not replace Account authentication.

Account Owner/Admin recovery can reset a forgotten Profile PIN according to policy.

## 12. Add/manage Profiles

The Profile picker may show `Add profile` only when:

- the Account is allowed to create another Profile;
- the instance profile limit allows it.

Detailed profile management belongs in Profile/Settings or Admin Users & Permissions, not in the picker.

`Manage profiles` may be a small secondary action but must not clutter normal selection.

## 13. Switch Profile

Entry points:

- Desktop account menu;
- Profile page;
- Mobile Profile area;
- TV profile/account menu.

`Switch profile` returns to the Profile picker without terminating the Account session.

Before switching, transient personal UI state may close, but durable progress must already use the active Profile and remain isolated.

The new active Profile immediately controls:

- visible media capabilities;
- navigation;
- progress;
- ratings;
- recommendations;
- personal settings;
- Connections;
- Learning state.

## 14. Logout

Logout ends the Account session and returns to Login.

Logout is distinct from:
- closing a Profile picker;
- switching Profiles;
- locking a Profile.

Where multiple authenticated Accounts on one device are supported later, that requires its own explicit account-switching contract rather than overloading Profile selection.

## 15. Visual style variants

Jularr supports one UX/layout contract with two visual skins.

The skin may change branding, decorative treatment and theme tokens. It must not change navigation, fields, authentication logic or Profile semantics.

### 15.1 Clean

Clean is the neutral/minimal Jularr design.

Login requirements:

- **no decorative background artwork**;
- plain Light/Dark app background;
- centered compact sign-in card/surface;
- Clean Jularr logo;
- default Clean accent is purple;
- restrained borders/shadows;
- no anime/Japanese scenery;
- no unnecessary text.

### 15.2 Original Jularr

Original Jularr is the richer Japanese-inspired visual skin.

Login may use:

- Japanese ink/watercolor scenery;
- cherry blossoms;
- mountain/torii/pagoda motifs;
- Original Jularr brand mark;
- restrained decorative background around the same central Login card.

Default Original accent is the established red/pink Jularr treatment.

Decoration must not reduce field readability or focus visibility.

## 16. Accent / hue shifting

Both Clean and Original use semantic design tokens rather than hard-coded page colors.

The chosen Accent can hue-shift the relevant branded/decorative tokens while preserving:

- contrast;
- semantic success/warning/error colors where meaning requires them;
- readable text;
- recognizable provider logos.

Examples:

- Clean default -> purple;
- Original default -> red/pink;
- another selected accent -> equivalent accent-derived buttons, focus rings and permitted decorative highlights.

Original cherry-blossom/decorative accent elements may shift coherently with the selected accent where visually appropriate.

Do not recolor third-party provider logos into misleading brand colors.

## 17. Light / Dark / System

Both visual styles support:

- Light;
- Dark;
- System.

Style and brightness mode are separate preferences.

Conceptually:

```text
Visual style: Clean | Original Jularr
Brightness: System | Light | Dark
Accent: default/preset/custom when supported
```

Do not fork components into separate Clean and Original implementations.

## 18. Branding

Instance branding may provide:

- instance name;
- allowed logo/brand override where supported;
- accent;
- default visual style.

If no custom branding exists:

- Clean uses the Clean Jularr mark;
- Original uses the Original Jularr mark.

Branding must not remove the ability to identify the configured Jularr instance where several instances are used.

## 19. Desktop

Desktop Login:

- full viewport;
- centered compact form;
- external provider grid below primary login;
- Passkey as compact secondary action;
- Clean has no decorative background;
- Original may use the approved Japanese decorative skin.

Desktop Profile Selection:

- centered profile grid;
- large enough avatars for immediate recognition;
- no sidebar/application shell until a Profile is active.

## 20. Mobile

Mobile Login:

- one column;
- no oversized decorative hero;
- external provider buttons may use a 2-column grid or full-width rows depending on width;
- form remains reachable with software keyboard;
- safe-area aware.

Mobile Profile Selection:

- compact profile grid/list;
- touch-sized targets;
- Profile PIN uses numeric keypad where appropriate.

## 21. TV

TV Profile Selection is first-class and remote-first.

Requirements:

- large profile avatars/names;
- strong focus ring;
- predictable left/right navigation;
- PIN keypad remote operable;
- Back returns safely;
- multiple Profiles normally show the picker after Account authentication.

TV Login should avoid long credential entry when an easier supported pairing/external authentication flow exists, but must not invent a separate identity model.

## 22. Loading / error states

Support:

- authentication in progress;
- invalid credentials;
- provider unavailable;
- provider callback failed;
- external identity already linked elsewhere;
- account disabled;
- no usable Profiles;
- Profile PIN incorrect/rate-limited;
- Profile permission changed;
- session expired.

Errors remain concise and actionable.

One failing external provider must not prevent password/passkey/other provider login methods from rendering.

## 23. Accessibility

- labels remain programmatically associated with fields;
- provider buttons include provider names, not icon-only;
- keyboard navigation and visible focus;
- TV focus is spatially predictable;
- PIN entry is screen-reader understandable;
- error text is announced;
- decorative Original artwork is ignored by accessibility APIs;
- selected Profile/focus state is not color-only.

## 24. Security boundaries

- every authenticated request resolves Account + active Profile server-side;
- Profile ID supplied by clients is validated against the authenticated Account/policy;
- switching Profile rotates/updates server-authoritative active-profile context safely;
- authorization never trusts a hidden/visible UI control;
- external provider callbacks validate state/nonce/redirect constraints;
- tokens/secrets are never logged;
- login/provider data is treated as untrusted external input;
- remember-session behavior uses secure session/token storage appropriate to the client.

## 25. Admin control

Admin determines:

- enabled account authentication methods;
- enabled external Login providers;
- whether external login may auto-create Accounts;
- default role/capabilities for auto-created Accounts;
- self-registration policy;
- whether multiple Profiles are allowed;
- profile count/default restrictions;
- optional default visual style/branding.

Personal Connections remain separate and appear only when both instance modules/policy and the active Profile allow them.

## 26. Must not implement

- no `Welcome back` heading;
- no explanatory marketing paragraph on Login;
- no decorative background in Clean;
- no duplicated Clean/Original page implementations;
- no provider identity as canonical Account/Profile ID;
- no automatic Admin/Owner rights from external login;
- no assumption that every external Connection is a Login provider;
- no automatic sync merely because the same provider authenticated the Account;
- no Profile PIN as account security replacement;
- no merging Progress/History/Ratings across Profiles;
- no roles/capability dump on the Profile picker;
- no Profile management dashboard inside the picker;
- no unavailable provider button;
- no hard-coded accent colors outside semantic theme tokens.

## 27. Approved mockup direction

Two Login references are expected in this folder:

1. **Clean** — purple default accent, Clean logo, plain/no decorative background, compact Login card with configured external integrations and optional Passkey.
2. **Original Jularr** — same information hierarchy and controls, Original logo, Japanese watercolor/cherry-blossom background, red/pink default accent, external integrations and optional Passkey.

The owner will upload the approved images.

Text specification wins over imagery on conflict.


---

## Plex-first Account Login and verified linking (approved planning extension, #911)

This is an explicit refinement of sections 5–7, not a new login/session model. Preserve the existing Jularr Account/Profile architecture, including pre-existing local users and all stored profile state.

Supported entry points:

1. **Sign in with Plex:** after a verified Plex provider authentication, look up a stable `(Plex, PlexAccountId)` login identity. An existing link signs in to that Jularr Account with the canonical session and Profile selection. If no link exists, show a neutral choice **I already have a Jularr account — sign in to link** or **Create my Jularr account**, without disclosing whether an email is already registered. Creating a new Account requires Admin-enabled auto-provisioning and applies safe default permissions/approval policy. An external-only Account does not require a fictitious local password.
2. **Link Plex while already signed in:** after step-up verification where appropriate, authenticate the Plex identity and explicitly confirm that it should be linked to the **current** Jularr Account. Keep the same Account ID, profiles, library/watch history, preferences, requests and privileges. If that verified Plex identity belongs to another Account, refuse; do not merge.
3. **Email matches:** matching Plex/Jularr email, username or display name is **never** sufficient to automatically merge or attach Accounts. Email is a hint, not proof. Requiring current-account login plus provider verification also avoids public Account-existence disclosure. A later merge of two already-created Jularr Accounts is a separate reviewed recovery process, not part of login.
4. **Login vs Connections:** Account-level linked login identity is separate from Profile-level Plex watchlist/progress/media Connection; using Plex Login must not silently enable sync. Disconnecting a Profile Connection must not remove the login method. Removing the last functional Account credential requires an explicit recovery path or must be denied.
5. **Security:** use supported Plex hosted authentication/PIN flow with single-use state and robust timeout/replay protection. Only verified stable Plex IDs are trusted; all tokens remain server-side. Disabled/unapproved local accounts cannot bypass policy by Plex sign-in. No Plex-originated Owner/Admin elevation.
6. Only render Plex Login when implemented, healthy, and enabled by Admin. The Plex app account and the Jularr Account remain different identities. Related implementation checklist: #911. Media item opening: #886.
