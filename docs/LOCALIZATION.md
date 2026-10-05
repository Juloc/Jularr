# Jularr localization architecture

Jularr uses one central UI translation catalog. UI language is independent from content language and from Learning courses.

This document is durable architecture guidance. GitHub Issues remain the backlog and source of task state; do not create a separate AGENDA.md or localization backlog file.

## Core rule

Normal page rendering never calls AI.

English source resources are defined in code with stable semantic keys. Missing or outdated translations are generated only by an explicit Owner action, persisted in SQLite, and reused afterwards.

## Error and validation localization (#853)

Errors use the same central localization model as other user-visible UI copy.

The backend/domain contract is a stable semantic error identity, not localized prose. A user-facing error may carry:

- a stable machine-readable error code when the client needs deterministic behavior;
- a semantic localization/message key;
- structured placeholder parameters;
- a safe incident/reference ID when a concrete technical incident was recorded.

The transport/UI boundary resolves the primary visible message through the normal locale bundle/fallback chain. Web, Android, TV and future clients MUST NOT branch on English or translated message text.

Internal diagnostics are not localized contracts. Stack traces, nested exception chains, SQL, unsafe filesystem details, raw sensitive provider payloads and secrets never become normal user-facing translated content.

Expected validation/not-found/conflict/forbidden outcomes use semantic result/error identities and appropriate localized UI messages. Unexpected technical failures may expose only the safe primary localized message plus the safe incident/reference ID (and stable code where needed); complete technical cause information remains Admin/server-side.

Do not throw localized strings from domain/persistence code as the canonical error contract. Do not create a second error-localization store beside `UiTranslationResources` / `UiTranslationCatalogStore`.

## Resource metadata

Every translatable resource must carry enough context for a translator or AI model to understand its purpose:

- stable semantic key such as player.repeatLine
- English source text
- feature/domain
- UI surface such as Button, Heading, Tooltip, Status, Error or Confirmation
- semantic description of what the UI element actually does
- tone/register guidance
- optional maximum-length guidance
- placeholder names plus the meaning of every placeholder
- terminology/do-not-translate constraints where required
- deterministic source hash derived from the complete translation context

A source/context change changes the hash. Generated or reviewed translations from the previous hash become Outdated. Manual translations remain manual and are never silently overwritten.

Do not use an English sentence as the resource key. Do not send isolated strings to AI without the metadata above.

## Translation lifecycle

Statuses are Missing, Generated, Reviewed, Manual and Outdated.

The Owner can manage locales at /Admin/Languages, generate Missing entries, regenerate Outdated entries, edit any translation manually, and mark Generated entries Reviewed.

AI output is validated before persistence. Required placeholders and protected terms must remain intact.

## Locale model

Locale identifiers are BCP-47/culture tags such as de, de-CH, id, ro, ja, pt-BR or ar.

Fallback is deterministic:

1. exact locale
2. parent culture/language
3. English source text

Example: de-CH → de → en.

Locale metadata includes native/English names and text direction. UI/CSS work must not assume left-to-right layout so RTL locales can be supported.

Dates, numbers and regional formatting belong to culture/locale formatting rather than AI translation.

## Catalog ownership

UiTranslationResources is the canonical source-resource catalog.

UiTranslationCatalogStore persists locale registrations and generated/reviewed/manual translations. It also synchronizes source metadata into SQLite so Admin UI can inspect the exact context sent to AI.

CodexCliProvider.GenerateUiTranslationsAsync is the first translation generator. The catalog contract is provider-independent; a later provider must consume the same contextual request and produce the same keyed result contract.

## Adding a resource

When adding visible fixed UI copy:

1. create a semantic key in the appropriate feature namespace;
2. provide source text plus semantic context metadata;
3. include placeholder meanings and protected terminology;
4. consume the catalog key from UI code;
5. do not add a second localization store or runtime AI fallback.

Example:

Key: learning.reviewDue
Source: {count} reviews due
Feature: Learning
Surface: Status
Description: Shows how many spaced-repetition review cards are currently due.
Tone: compact status
Placeholder count: current due review count

## Relationship to Learning v2

Localization and learning languages are separate concepts.

A user may use Jularr UI in German, Japanese → Indonesian as one Learning course, German → Indonesian as another Learning course, or no Learning system at all.

Learning capability visibility is resolved independently through the Learning v2 profile → media type → work/series → content inheritance hierarchy tracked in issue #226.

## Runtime selection

The Owner registers and generates application locales at /Admin/Languages. Each authenticated profile can choose one of those enabled locales at /Settings/Language.

The profile choice is stored in UiProfileLocales. Runtime UI reads use UiTranslationCatalogStore.LoadProfileBundleAsync: persisted exact-locale values override parent-locale values, which override the English source resource. Runtime reads never invoke the AI generator.

UiRequestLocalization.GetBundleAsync resolves the bundle once per request and caches it in HttpContext.Items, so a page model and the shared layout share the same result:

- signed-in requests use the profile locale;
- anonymous requests (setup, sign-in, registration) negotiate the browser Accept-Language header in q-value order against the Owner-enabled locales, matching the exact tag first and then its parent culture (de-DE → de), and otherwise use English. These responses send Vary: Accept-Language.

Page models that render catalog text in their own view call GetBundleAsync in the handler (a view renders before the layout, so ViewData["UiTextBundle"] is not yet set while the page body renders).

The shared layout applies the resolved BCP-47 lang and text direction. Shell CSS uses logical properties (inline start/end) so RTL locales do not need a separate layout.

PWA runtime text (install and update notices, connectivity toasts, player window actions) lives under pwa.* keys. The layout serializes exactly that subset into a JSON script element that pwa.js reads; JavaScript never keeps its own copy of UI strings. The web manifest is a static, install-time file and stays in the English source language.

Migrated surfaces: shared navigation and layout, setup/sign-in/registration, the Settings index, Home and Learning. Remaining hardcoded fixed UI text must move incrementally to the same catalog rather than creating page-specific localization systems. Tests fail when a literal catalog key used in source is missing from UiTranslationResources.

## Shipping additional locales

Jularr ships only the English source resources. German (de), Indonesian (id) or any other locale is not seeded into the database, because shipped values would be a second translation source next to the Owner-managed catalog and would bypass the Generated/Reviewed/Manual lifecycle.

To offer a locale, the Owner:

1. opens /Admin/Languages and adds the locale tag, for example de or id;
2. runs Generate missing (the configured AI provider receives each key with its full context metadata);
3. checks the result and uses Mark reviewed, or edits entries manually where wording must differ;
4. after an upgrade that changes source text or context, runs Regenerate outdated.

Once enabled, the locale is selectable at /Settings/Language and is used automatically for anonymous pages when the browser prefers it.
