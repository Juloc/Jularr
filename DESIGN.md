---
name: Jularr
description: A calm, paper-and-ink home for everything a household watches, reads and listens to.
colors:
  accent: "#7c3aed"
  accent-hover: "#6f24db"
  accent-soft: "#eae5ff"
  accent-border: "#c0b2f8"
  accent-text: "#6d3bcc"
  on-accent: "#ffffff"
  paper: "#f2f2f7"
  surface: "#fcfcff"
  surface-2: "#eeedf2"
  surface-3: "#e4e3e8"
  surface-raised: "#f7f6fb"
  sidebar-wash: "#f8f7fc"
  hairline: "#d6d5da"
  hairline-strong: "#bebdc2"
  ink: "#18181c"
  ink-secondary: "#4a4a4e"
  ink-muted: "#636267"
  night-paper: "#0e0e11"
  night-surface: "#17171a"
  night-surface-2: "#202024"
  night-surface-3: "#2a292d"
  night-hairline: "#323135"
  night-ink: "#f0eff5"
  night-ink-secondary: "#c4c3c8"
  night-ink-muted: "#a1a1a6"
  night-accent-text: "#b38aff"
  night-accent-soft: "#2b2445"
  success-text: "#1f6b37"
  danger-text: "#a12a2a"
  warning-text: "#7a5300"
  info-text: "#255aa3"
  jularr-red: "#c8102e"
typography:
  display:
    fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif"
    fontSize: "clamp(28px, 4vw, 44px)"
    fontWeight: 800
    lineHeight: 1.05
    letterSpacing: "-0.035em"
  headline:
    fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif"
    fontSize: "17px"
    fontWeight: 700
    lineHeight: 1.25
  title:
    fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: 1.4
  body:
    fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif"
    fontSize: "12px"
    fontWeight: 700
    lineHeight: 1.3
rounded:
  xs: "6px"
  sm: "8px"
  md: "9px"
  lg: "12px"
  card: "14px"
  pill: "999px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "14px"
  lg: "18px"
  xl: "28px"
  page-x: "42px"
components:
  button-default:
    backgroundColor: "{colors.surface-2}"
    textColor: "{colors.ink}"
    typography: "{typography.label}"
    rounded: "{rounded.md}"
    padding: "9px 14px"
  button-default-hover:
    backgroundColor: "{colors.surface-3}"
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    typography: "{typography.label}"
    rounded: "{rounded.md}"
    padding: "9px 14px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
  chip:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink-muted}"
    typography: "{typography.label}"
    rounded: "{rounded.pill}"
    padding: "6px 10px"
  chip-selected:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.pill}"
  panel:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.card}"
    padding: "18px"
  nav-item:
    textColor: "{colors.ink-muted}"
    typography: "{typography.title}"
    rounded: "{rounded.md}"
    padding: "10px 11px"
  nav-item-active:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
---

# Design System: Jularr

## Overview

**Creative North Star: "The Ink & Paper Library"**

Jularr looks like a well-kept shelf in a quiet room. The ground is a faintly tinted paper (a washi texture sits behind everything at low opacity), surfaces are thin-ruled and softly lifted, and the cover artwork of the media is always the loudest thing on screen. The interface steps back: it is calm and restrained, warm and slightly handmade, never a dashboard and never a streaming-service showroom.

Colour comes from one accent seed per profile. Everything else, the neutrals, the soft fills, the chart colours and even the tint of the ink artwork, is derived from that seed, so the system stays coherent whatever the user picks. The default seed is Jularr lila (#7c3aed). Two skins share one layout: Clean (neutral surfaces, no decorative art) and Original Jularr (ink landscapes, cherry blossom, red/pink character, and an optional mascot). The mascot and sakura effects are playful accents that belong to the Original skin only.

Density follows the audience. Consumer surfaces are spacious and sparse, with copy kept to what is needed. Admin surfaces are information-dense but structured, on the same tokens and without decorative art.

**Key Characteristics:**
- Tinted paper neutrals, a single derived accent, thin 1px rules.
- Soft layered cards, never hard edges and never glass or neon.
- Media artwork is the visual focus; the interface recedes.
- Light, Dark and System are all designed, not inverted.
- System font stack throughout. There are no web fonts.
- Optional decorative layer (washi, ink, blossom, mascot) that skins can switch off without changing layout.

## Colors

A paper-and-ink palette. Neutrals carry a whisper of the accent hue (chroma at most 0.007). The accent appears on active navigation, primary actions, selected chips and progress.

The values below are for the default lila seed. Every neutral and accent token is derived per profile by `Features/Appearance/AccentPalette.cs` and verified for WCAG contrast, so these hex values are the default instance, not fixed constants.

### Primary
- **Jularr Lila** (#7c3aed): the default accent. Active nav item, primary button, selected chip, progress fill, and the play brand mark. White text sits on it (on-accent #ffffff).
- **Lila Deepened** (#6f24db): hover state of every accent fill.
- **Lila Mist** (#eae5ff / dark #2b2445): soft accent wash behind hovered chips and highlighted rows.
- **Lila Rule** (#c0b2f8): accent-tinted border on hovered chips.
- **Lila Ink** (#6d3bcc / dark #b38aff): accent colour for text and focus rings. It is contrast-checked against every surface, unlike the solid accent.

### Neutral
- **Rice Paper** (#f2f2f7 / dark #0e0e11): page background under the washi texture.
- **Clean Sheet** (#fcfcff / dark #17171a): panels, cards and raised surfaces.
- **Pressed Sheet** (#eeedf2 / dark #202024) and **Shadowed Sheet** (#e4e3e8 / dark #2a292d): secondary buttons, hovers, tracks.
- **Hairline** (#d6d5da / dark #323135) and **Strong Hairline** (#bebdc2): the 1px border used on almost every surface.
- **Sumi Ink** (#18181c / dark #f0eff5): primary text. Secondary text is #4a4a4e / #c4c3c8 and muted text is #636267 / #a1a1a6.

### Status
- Success #1f6b37, danger #a12a2a, warning #7a5300, info #255aa3. They are fixed per mode (lighter tints in dark) and never follow the accent hue.

### Brand
- **Ink-Seal Red** (#c8102e): the red the original ink-and-paper artwork is painted in. It remains a selectable preset (named jularr) but is no longer the default. Artwork is tinted to the profile accent with a hue-rotate, so no raster art is re-exported.

### Named Rules
**The Derived Accent Rule.** Never hard-code an accent or a tinted neutral. Consume the semantic tokens (`--accent`, `--surface`, `--muted` and the rest) so the profile's seed reaches every surface.

**The Accent Is Rare Rule.** The solid accent marks what is active or primary on a screen and nothing else. Cards, rows and status tags stay neutral with borders and light tints. Status is never a saturated pill.

**The Dark Media Stage Rule.** Hero, video and poster surfaces stay deliberately dark in both themes, with their own light text tokens (#f3f5f7 / #c5cbd3), so artwork keeps its contrast.

## Typography

**Display, Body and Label Font:** the system UI stack (system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif). There is one family and no web-font loading.

**Mono:** ui-monospace, SFMono-Regular, Menlo, Consolas for code, hashes and diagnostics.

**Reading fonts** are user-chosen in the Reader (serif, sans, custom) through `--reader-font-family` and `--book-reader-font`. They never affect the application shell.

**Character:** Plain and native-feeling, with weight and tight tracking doing the work that a display face would. Hierarchy comes from weight (800 titles, 700 labels) and tint (text, secondary, muted).

### Hierarchy
- **Display** (800, clamp(28px, 4vw, 44px), 1.05, -0.035em): page titles and the home hero.
- **Headline** (700, 17px): panel headings.
- **Title** (600, 14px): navigation, list titles and card titles.
- **Body** (400, 13px, about 1.5): the working size of the app. Secondary copy is 12px.
- **Label** (700, 12px): buttons and chips. Metadata and counters go down to 11px, with tabular numerals for numbers.

### Named Rules
**The Weight Over Size Rule.** The scale is tight (11 to 18px for most of the UI). Create emphasis with weight and tint, not with another size.

## Layout

A fixed 236px sidebar on desktop and a bottom navigation bar on mobile. The main area is padded 34px top and 42px sides on desktop and 24px and 18px at 520px width and below. Safe-area insets are added everywhere for the PWA. Content stacks in panels and grids.

Spacing follows a small ad-hoc rhythm of 4, 8, 12, 14, 18, 24 and 28px. There is no strict base-grid token set. Breakpoints cluster at 520, 560, 620, 640, 720, 760, 820 and 900px, with 720px as the dominant mobile switch. Containers use `min-width: 0` and logical properties (`inset-inline-start`, `margin-inline-start`), because the layout is RTL-ready.

Consumer pages are sparse and put the media artwork first. Admin pages may be dense and offer a Compact density mode. TV is browse-first with a focus model of its own, and Admin is never on TV.

## Elevation & Depth

Hybrid: tonal layering first, one soft shadow second. Surfaces separate through the paper, sheet and pressed-sheet tones and a 1px hairline. Cards add a single two-part ambient shadow, and nothing is ever hard-edged.

### Shadow Vocabulary
- **Card** (`box-shadow: 0 1px 2px rgba(20,16,12,.05), 0 8px 24px rgba(20,16,12,.06)`, dark `0 1px 2px rgba(0,0,0,.3), 0 10px 30px rgba(0,0,0,.28)`): panels, hero, raised cards via `--shadow-card`.
- **Accent Glow** (`0 6px 16px -8px var(--accent-glow)`): under primary buttons and the active nav item.
- **Overlay** (`0 30px 80px rgba(0,0,0,.35)`): dialogs and drawers only.

### Named Rules
**The One Soft Shadow Rule.** Use `--shadow-card` for resting depth. A heavier shadow is reserved for floating overlays.

## Shapes

Gently curved, never sharp and never fully round except for chips and avatars. Buttons and nav items use 9px, inputs and rows 8 to 12px, cards and panels 14px (`--radius-card`), and the hero adds 4px to that. Chips, toggles and progress bars are pills (999px). Cover art keeps a fixed 2:3 poster ratio with a 6px corner. Borders are 1px hairlines, and focus uses a 2px accent outline.

## Components

### Buttons
- **Shape:** gently curved (9px), 13px bold label, padding 9px 14px.
- **Default:** pressed-sheet fill with hairline border, one tone darker on hover.
- **Primary:** accent fill with on-accent text, a border mixed 82% toward text colour and a soft accent glow below. Hover deepens to the accent-hover tone.
- **Disabled:** 50% opacity and no hover reaction.

### Chips
- **Style:** pill, transparent with hairline border and muted text.
- **State:** hover gets the accent wash and accent-tinted rule. Selected becomes a solid accent fill.

### Cards / Panels
- **Corner Style:** 14px.
- **Background:** clean sheet with a 1px hairline.
- **Shadow Strategy:** the single card shadow from Elevation & Depth.
- **Internal Padding:** 18px, with a heading row that is 14px above content.

### Inputs / Fields
- **Style:** field-background fill, 1px hairline, 8 to 10px radius.
- **Focus:** the border shifts toward the accent (50% mix), and wrapper-level focus-within is used where the input has no own border.
- **Error / Disabled:** status text colours and a muted fill. Some inputs remove the native outline and rely on this border shift (see the audit notes).

### Navigation
- **Style:** sidebar with a translucent washi tint and blur (10px). Items are 14px semibold muted text with 9px radius.
- **Active:** solid accent fill, on-accent text and the accent glow.
- **Mobile:** a bottom bar of icon items at 10.5px labels.

### Home Hero (signature)
An ink-landscape banner (`/brand/hero-fuji.webp`) behind a left-to-right surface fade, with the brand lockup and, in the Original skin, the heroine art cropped at the right edge. Body copy stays in the surface-faded left half.

## Do's and Don'ts

### Do:
- **Do** read colours from semantic tokens (`--accent`, `--surface`, `--muted`, `--border`) so the profile accent and both themes work.
- **Do** keep the accent for active, selected and primary states, and let neutral surfaces carry everything else.
- **Do** let media artwork lead and keep the interface quiet around it.
- **Do** design Light, Dark and System together, and give every screen its loading, empty, partial, error, forbidden and offline state.
- **Do** keep touch targets at least 44px on consumer and TV surfaces and give every interactive element a visible focus.
- **Do** use logical CSS properties so layouts mirror under RTL.

### Don't:
- **Don't** hard-code accent or neutral hex values in components. The Derived Accent Rule explains why.
- **Don't** add left or one-sided accent stripes to cards and rows, and don't add decorative eyebrow labels above headings.
- **Don't** use glass, neon or saturated status pills. Status uses light tints and a border.
- **Don't** add copy that repeats a heading or states the obvious, such as welcome text, marketing lines and numbered setup steps.
- **Don't** animate layout properties such as width and height. Use transform and opacity, and honour `prefers-reduced-motion`.
- **Don't** add fake console or hardware frames to game players, or change the layout between the Clean and Original skins.
