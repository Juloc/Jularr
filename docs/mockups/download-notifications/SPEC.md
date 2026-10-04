# Download Toasts & Notification Events

This specification defines the **Offline-domain event semantics and context** for consumer offline downloads. Canonical Notification infrastructure owns the durable inbox/Bell projection, transient attention/Toast policy, foreground/background deduplication and supported operating-system delivery.

Approved visual references for this surface live in this folder. If image and text conflict, this specification wins.

Related:
- `docs/mockups/download-selection/SPEC.md` — explicit download creation
- `docs/mockups/download-quick-view/SPEC.md` — live operational download state
- `docs/mockups/downloads-offline/SPEC.md` — full current-device manager
- `docs/mockups/offline-settings/SPEC.md` — notification category entry point and offline policy
- canonical Jularr Notification contracts — durable inbox/Bell, Toast attention, action resolution and external delivery. When the dedicated Notifications planning work is integrated, its `docs/NOTIFICATIONS.md` and notification surface specs are authoritative for shared presentation behavior.

## 1. Product role

Download events have three distinct presentation channels:

1. **Download Quick View** — live operational state and direct controls.
2. **Toast / Snackbar** — short immediate feedback while Jularr is in the foreground.
3. **Bell / Notification Center** — relevant events that remain useful after the moment has passed.

Supported clients may additionally emit an **OS/system notification** when Jularr is not foregrounded.

These are presentations of the same canonical event/state transition. They must not create duplicate business events merely because more than one presentation channel is used.

## 2. Canonical event ownership

The download/offline subsystem emits canonical transitions such as:
- explicit download accepted;
- waiting for eligible network;
- download completed and verified;
- download failed;
- storage admission failed;
- storage location unavailable;
- Smart Offline batch prepared;
- Smart Offline requires user attention.

Toast, Bell and OS notification adapters consume those transitions.

Do not create:
- a second download-status owner;
- page-local notification state;
- duplicate durable Bell entries for the same event.

## 2.1 Shared Notification ownership boundary

Offline owns:
- the fact that a local Offline transition occurred;
- subject/media identity needed for user-facing context;
- safe domain actions such as Retry or opening Downloads;
- severity/action-required meaning specific to Offline state.

The canonical Notification system owns:
- profile subscription/preferences;
- durable inbox persistence;
- read/unread/dismissal;
- Bell count and Bell/Center queries;
- transient Toast queue/dedup/attention policy;
- foreground/background and immersive-mode suppression;
- Quiet Hours/Digest/external delivery;
- canonical notification action resolution.

Do not create an Offline-specific NotificationStore, Bell model or Toast presenter.

## 3. Channel responsibilities

### Quick View
Use for:
- progress;
- pause/resume;
- retry;
- current queue;
- waiting/verifying state.

### Toast / Snackbar
Use for:
- immediate confirmation;
- immediate success;
- immediate actionable failure;
- short-lived state transition feedback.

### Bell / Notification Center
Use for:
- completed downloads when the user's notification settings allow;
- failures;
- storage/action-required states;
- optional Smart Offline summaries.

### OS/system notification
Use when:
- the platform supports it;
- permission/category is enabled;
- Jularr is backgrounded/closed or foreground presentation would otherwise be missed.

Avoid foreground duplication when an in-app toast already provides the needed feedback.

## 4. Event matrix

| Event | In-app toast | Bell entry | OS notification |
| --- | --- | --- | --- |
| Explicit download accepted | Yes | No | No |
| Queued for Wi-Fi/unmetered | Yes, once | No | No |
| Normal progress | No | No | No |
| Pause/resume initiated by user | No by default | No | No |
| Verified download completed | Yes | Configurable | Background/configurable |
| Download failed | Yes | Yes | Background/configurable |
| Insufficient offline storage | Yes | Yes | Background/configurable |
| Storage location unavailable | Yes | Yes | Background/configurable |
| Individual Smart Offline item completed | No | No | No |
| Smart Offline batch prepared | Optional/configurable | Optional/configurable | Background/configurable |
| Smart Offline requires action | Yes | Yes | Background/configurable |

The product should favor silence over repetitive routine messages.

## 5. Explicit download accepted

After the download-selection flow confirms successfully, show one foreground toast.

Examples:

**3 Episoden wurden zu Downloads hinzugefügt**

Secondary copy:
`Startet über WLAN.`

or, when eligible immediately:

**3 Episoden werden heruntergeladen**

Optional action:
- **Downloads anzeigen**

Do not create a Bell notification merely for accepting a normal user action.

## 6. Queued for Wi-Fi / unmetered network

When a user explicitly starts a download but current network policy prevents transfer:

**Download vorgemerkt**

`Startet automatisch im WLAN.`

Action:
- **Downloads anzeigen**

This toast is emitted once for the transition into the waiting state.

Do not repeat it periodically while the job remains waiting.

This is not an error and should not create a Bell entry.

## 7. Verified completion

Completion is emitted only when the offline package is genuinely Ready.

Required sequence conceptually:

`Transferred -> Verified/Finalized -> Ready -> completion event`

Do not show completion at a raw 100% byte-transfer value while verification/finalization is still pending.

### Single item examples

Video:
**Download abgeschlossen**  
`Dune: Part Two ist offline verfügbar.`

Primary action:
- **Abspielen**

Reading:
**Download abgeschlossen**  
`Atomic Habits ist offline verfügbar.`

Primary action:
- **Lesen** or **Weiterlesen** when appropriate.

### Batch examples

**3 Episoden offline verfügbar**  
`Frieren · Episoden 7–9`

Action:
- **Ansehen**

## 8. Completion grouping

Do not emit one toast/OS notification per child item when several complete as one user-relevant batch.

Examples:

**Frieren · Staffel 1**  
`8 Episoden sind jetzt offline verfügbar.`

or cross-work aggregation:

**5 Downloads abgeschlossen**  
`Frieren, Dune und 3 weitere Inhalte sind offline verfügbar.`

Action:
- **Downloads anzeigen**

Grouping is presentation logic over canonical events; it must not create a new durable batch domain owner.

## 9. Completion Bell behavior

A completion may appear in the Bell when enabled by the user's central notification preferences.

Example:

**Download abgeschlossen**  
`Dune: Part Two ist offline verfügbar.`  
`vor 2 Min.`

Selecting the entry opens the most useful consumer destination:
- Player;
- Reader;
- media detail;
- Downloads & Offline for grouped completions.

Bell read/unread state belongs to the existing notification owner, not to the offline subsystem.

## 10. Download failure

Foreground toast:

**Download fehlgeschlagen**

`Kaiju No. 8 · Episode 1 konnte nicht heruntergeladen werden.`

Primary action:
- **Erneut versuchen**

Secondary:
- **Downloads anzeigen**

A failure also creates a Bell event because the user may need to return later.

Consumer copy may explain a safe high-level cause, e.g.:
- `Verbindung zum Server fehlgeschlagen.`
- `Quelle ist momentan nicht verfügbar.`

Never expose:
- stack traces;
- HTTP status codes;
- hashes;
- file paths;
- indexers;
- download-client internals.

## 11. Insufficient storage

Foreground toast:

**Nicht genügend Offline-Speicher**

`Für Frieren · Episode 9 werden weitere 1,8 GB benötigt.`

Primary action:
- **Speicher verwalten**

Secondary:
- **Offline-Einstellungen**

This creates a Bell entry because user action is required.

Do not automatically remove explicit downloads in response.

## 12. Storage location unavailable

Native clients may emit:

**Offline-Speicher nicht verfügbar**

`Der ausgewählte Speicherort ist momentan nicht erreichbar.`

Primary action:
- **Speicher verwalten**

Optional:
- **Erneut prüfen**

This creates a Bell entry.

Do not silently move the job to another location or duplicate content elsewhere.

## 13. Temporary connectivity loss

A currently running download that simply transitions to:

**Wartet auf Verbindung**

normally emits no toast, no Bell event and no OS notification.

The Quick View is sufficient.

Only emit an event if the canonical state becomes an actual failure or user action becomes necessary.

## 14. Pause/resume

User-triggered Pause/Resume does not need a toast by default.

Immediate visible state change in Quick View/manager is sufficient.

A toast is allowed only if the initiating control disappears immediately and the user would otherwise receive no confirmation, but this should not be the baseline behavior.

No Bell event.

## 15. Smart Offline completion behavior

Smart Offline must be significantly quieter than explicit downloads.

Do not notify for every automatically prefetched:
- episode;
- chapter;
- audiobook chapter;
- individual package.

Optional configurable batch summary:

**Smart Offline aktualisiert**

`3 neue Episoden sind für unterwegs verfügbar.`

Action:
- **Offline-Inhalte ansehen**

This may create a Bell/OS notification only if that category is enabled.

## 16. Smart Offline attention state

Temporary speculative failures should normally be retried silently.

Notify only when user action is actually required.

Example:

**Smart Offline pausiert**

`Nicht genügend Offline-Speicher.`

Primary action:
- **Speicher verwalten**

This is Bell-relevant.

## 17. Toast duration

Success/informational toasts:
- auto-dismiss after a short readable duration;
- remain long enough to perceive title, summary and one action.

Action-required failure toasts:
- may remain longer;
- must still be dismissible;
- durable follow-up exists in Bell/Quick View when relevant.

Do not keep permanent toasts on screen.

## 18. Toast stacking

Normal product usage should not create a tall stack of simultaneous routine download toasts.

Rules:
- aggregate closely related child events;
- replace/update compatible transient toasts where appropriate;
- cap visible concurrent toasts according to the shared Jularr toast system;
- preserve the highest-severity/action-required item.

The approved reference may visually demonstrate several variants together for design documentation, but production behavior should avoid such a stack under normal use.

## 19. Toast placement

### Desktop
Use the shared global toast region, preferably upper-right below/clear of the top app bar utilities.

It must not cover:
- global Download Indicator;
- Notification Bell;
- critical modal actions.

### Mobile
Placement is owned by the shared canonical Notification Toast component rather than the Offline subsystem.

The current canonical target uses a compact **top in-app banner/toast** while Jularr is foregrounded on phone-sized layouts. It must respect the app bar/safe area and must not obscure:
- Player/Reader immersive controls;
- critical sheet/modal actions;
- system safe areas.

The approved Offline reference that shows a bottom snackbar remains an **event-content example**, not placement authority. Canonical Notification layout wins when the two planning branches are integrated.

## 20. Toast content anatomy

Keep each toast compact:

- semantic icon;
- title;
- one-line or short two-line context;
- at most one obvious primary inline action;
- dismiss control when appropriate.

Do not add progress bars to completion/failure toasts. Live progress belongs to Quick View.

## 21. Severity

### Success
Verified completion.

### Neutral/info
Download accepted, queued for Wi-Fi.

### Warning
Storage nearing/at required limit, Smart Offline paused for policy reasons.

### Error
Download failed or storage location unavailable.

Color is supplemental only; icon/text must carry the meaning.

## 22. Relationship to the Download Indicator

A completion may cause the global Download Indicator to transition/disappear after the recently-completed relevance window.

The toast does not own that lifecycle.

A failure toast and Bell event may appear while the Download Indicator continues to show a failed attention state.

These surfaces must remain synchronized because they read the same canonical state/event source.

## 23. Relationship to Bell unread state

When an event is configured to create a Bell entry:
- Bell unread count/dot follows the existing Notification Center rules;
- dismissing a toast does not mark the Bell event read;
- opening the Quick View does not automatically mark unrelated Bell events read;
- selecting the Bell entry follows normal read semantics.

## 24. OS/system notification behavior

Supported environments may include:
- Android system notifications;
- PWA/browser notifications;
- future native desktop notifications.

Use OS notifications primarily when Jularr is not foregrounded.

Examples:

**Jularr — Download abgeschlossen**  
`Dune: Part Two ist offline verfügbar.`

Possible platform actions:
- **Abspielen**
- **Downloads**

Failure:

**Jularr — Download fehlgeschlagen**  
`Kaiju No. 8 · Episode 1`

Possible action:
- **Erneut versuchen**

Only expose actions that can be executed safely from the platform notification context.

## 25. Foreground deduplication

When Jularr is foregrounded and an in-app toast is visible, avoid also showing an OS notification for the exact same event unless platform behavior requires it.

The Bell event may still be created independently according to user preferences.

Goal:
- immediate feedback once;
- durable history once;
- no duplicate alert storm.

## 26. Notification preferences

Offline Settings does not own a second notification system.

It deep-links to the central notification preferences.

Relevant categories may include:
- Download completed;
- Download failed;
- Offline storage needs attention;
- Smart Offline summary.

The central notification owner determines:
- in-app Bell persistence;
- OS channel permission/category behavior;
- quieting/muting policy.

The offline subsystem supplies event semantics and context.

## 27. Deep-link destinations

### Download accepted
**Downloads anzeigen** -> Downloads & Offline, focused on active queue.

### Completion
Single video/audio -> Player/detail as appropriate.  
Single Reading -> Reader/detail.  
Grouped completion -> Downloads & Offline / Offline available.

### Failure
**Erneut versuchen** -> retry action when safe; otherwise focus failed item in manager.

### Storage issue
**Speicher verwalten** -> Downloads & Offline storage context.  
**Offline-Einstellungen** -> Offline Settings.

## 28. Retry action safety

A toast/OS notification may offer Retry only when:
- the canonical failed job still exists;
- the retry action is idempotent/safe;
- required profile/account context is active or can be safely resolved.

If not, action should open the failed item in the full manager instead.

## 29. Account/profile isolation

Event content is scoped to the owning account/profile.

On profile switch:
- do not surface another profile's new toasts;
- Bell entries follow the existing profile/account visibility rules;
- OS notification tap must revalidate access before opening media or executing Retry.

Do not leak title/artwork from inaccessible profile-local content.

## 30. Offline behavior

Notifications are generated from local state transitions when possible.

A verified local completion can be surfaced even while the server is unreachable.

Actions that require server access degrade gracefully:
- open local ready content if available;
- queue/retry later;
- explain unavailability only when needed.

## 31. Restart behavior

Durable Bell events are reconstructed from the canonical notification owner.

Transient toasts are not replayed after restart merely because they existed before shutdown.

A failure that still requires attention remains discoverable through:
- Download Indicator;
- Downloads & Offline;
- Bell entry when configured.

## 32. Accessibility

- use shared live-region semantics for toast announcements;
- success/info should not aggressively interrupt screen readers;
- errors requiring action may use a stronger announcement appropriate to the shared component;
- actionable controls have clear accessible labels;
- dismiss is reachable by keyboard/touch;
- timeout must comply with shared accessibility behavior when focus/assistive technology interacts with the toast;
- meaning must not rely on color alone.

## 33. Localization

All copy uses the normal Jularr localization catalog.

Shared terms must match other offline surfaces:
- Download;
- Offline verfügbar;
- Smart Offline;
- Erneut versuchen;
- Speicher verwalten;
- Downloads anzeigen.

Batch/plural copy must use proper locale-aware pluralization.

## 34. Visual direction

Approved reference direction:
- Jularr Clean Purple light theme;
- Desktop normal Jularr Home context with global download indicator and Bell visible;
- one prominent successful completion toast is the canonical production reference;
- other toast variants may be illustrated for documentation but should not imply they normally stack simultaneously;
- Mobile shows a compact bottom snackbar above navigation:
  - success icon;
  - **3 Episoden offline verfügbar**;
  - context `Frieren · Episoden 7–9`;
  - **Ansehen** action;
- Bell shows an unread indicator when the event is configured as durable.

Dark mode preserves the same hierarchy and severity distinctions.

## 35. Explicitly out of scope

Do not define here:
- the complete Notification Center screen;
- notification read/unread architecture;
- central notification settings UI;
- queue progress rendering;
- pause/resume UI;
- download quality/track selection;
- storage cleanup policy;
- Admin notification channels;
- Admin acquisition/download notifications.

This spec only defines **consumer offline-download event semantics and presentation routing**.

## 36. Acceptance criteria

- [ ] Explicit download acceptance gives immediate foreground feedback without creating Bell spam.
- [ ] Waiting for Wi-Fi is informational, emitted once and not treated as failure.
- [ ] Completion is emitted only after verification/finalization reaches Ready.
- [ ] Multiple related child completions are grouped rather than spamming the user.
- [ ] Failures and storage/action-required conditions produce durable Bell events.
- [ ] Temporary connectivity loss does not produce unnecessary notifications.
- [ ] Smart Offline is quiet by default and only summarizes batches or surfaces action-required states.
- [ ] Foreground toast and OS notification are deduplicated.
- [ ] Bell persistence/read state remains owned by the existing Notification system.
- [ ] Deep links lead to the appropriate Download manager/media/storage context.
- [ ] Retry actions are safe/idempotent and revalidate account/profile context.
- [ ] No technical downloader/server internals are exposed to consumers.
- [ ] Desktop and Mobile use the shared Jularr toast/snackbar visual system.
