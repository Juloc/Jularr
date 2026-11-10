# Localization and languages

UI texts and locales, language packs, instance and profile languages.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- English source declarations in C# are **registration inputs**, not a second runtime dictionary or a UI fallback.
  During host startup, invoke the canonical Backend `AddJularrLocalization(UiResources.English)` registration.
  Its hosted synchronization idempotently INSERTs/UPDATEs source keys and English text in **PostgreSQL** using the existing Logic transaction.
  It is **not** an EF migration, not a manual per-feature SQL file and not a second locale store.
  Schema creation/migration remains the separately controlled Backend/database baseline.
- Human-editable AI context/semantic descriptions, translations, locale enablement, versions and statuses are stored **only in PostgreSQL**.
  Do not hard-code descriptions or non-English translations in Frontend.
  Description/translation editing and AI generation belong to authorized Backend/Admin workflows; new DB descriptions remain awaiting admin review
  until that contract exists.
  Never fabricate translations.
- Every normal Razor request resolves an enabled locale from Backend locale metadata and a complete translated pack through the existing
  `ReadUiLocalesV1`/`ReadUiTextsV1` services and bounded Backend cache.
  Exact locale -> parent -> English fallback is handled centrally.
  There is **no SQL per label**, no per-page browser translation download, and no AI generation during rendering.
- Verified active-profile `UiLocaleId` takes precedence over the stored instance default.
  The shared Backend language-pack service resolves enabled exact/parent locale and English fallback; browser hints do not override configured
  defaults.
  Offline language bundles for all local profiles are future-only.
- Apply the resolved request culture around downstream rendering in the host middleware.
  Setting culture inside an awaited initialization method alone does not establish the caller's rendering culture.
  Never localize IDs or wire values.

## Backend

- [ReadUiTextsV1.cs](../../src/Jularr.Backend/Service/Public/Localization/V1/ReadUiTextsV1.cs),
  [ReadUiLocalesV1.cs](../../src/Jularr.Backend/Service/Public/Localization/V1/ReadUiLocalesV1.cs): canonical Public reads for Razor/API; SQL resolves
  fallback and aggregates the complete language pack.
  [UiLocalizationCache.cs](../../src/Jularr.Backend/Service/Public/Localization/V1/UiLocalizationCache.cs) is passive caching;
  [registration](../../src/Jularr.Backend/Service/Core/Localization/UiLocalizationRegistration.cs) synchronizes English declarations at startup.
- [UiLocalizationLogic.cs](../../src/Jularr.Backend/Logic/Localization/UiLocalizationLogic.cs): source sync, shipped catalogs and canonical
  description/translation mutations (shipped texts from [Catalogs](../../src/Jularr.Frontend/Localization/Catalogs) through
  [UiCatalogs.cs](../../src/Jularr.Frontend/Localization/UiCatalogs.cs) fill missing, machine and outdated texts at startup when their English still
  matches, never what someone wrote; German is reviewed and enabled, French, Spanish, Italian, Dutch, Polish, Portuguese, Japanese and Indonesian are
  machine texts, off until the Owner enables them); [host contract and gaps](../../README.md#gemeinsamer-postgresql-textkatalog).
- [Admin localization services](../../src/Jularr.Backend/Service/Admin/Localization/V1/UiMessageServices.cs), [versioned catalog
  contracts](../../src/Jularr.Backend/Data/Admin/Localization/V1/UiMessages.cs): paged editable reads and Owner description/manual translation
  updates.
- [UiLocaleServices.cs](../../src/Jularr.Backend/Service/Admin/Localization/V1/UiLocaleServices.cs),
  [UiLocales.cs](../../src/Jularr.Backend/Data/Admin/Localization/V1/UiLocales.cs): Owner language registry editing; canonical field format and
  localization writer.
- Languages: the instance language and the enabled `UiLocales` are the Owner's; each profile picks one of them.
  `ProfileWorks` gives every profile a Work's `Title` and `Overview` in its language (a manual or provider title of that locale, else the canonical
  title; a summary of that locale first) and user pages read them.
  Imports keep `Works.OriginalLanguage` (TMDB `original_language`, AniList country of origin, the earliest Open Library edition) and other languages'
  texts as `WorkMetadataData.Translations` (TMDB translations, region before language; Open Library edition titles).
  An untagged book release is in the book's original language; one in none of the request's languages is taken immediately and can later upgrade to a
  requested language under the same profile (quality groups and format score still rank first), and the request says it reads translated
  (`ReadsTranslated`).
- [Data/Public/Localization/V1](../../src/Jularr.Backend/Data/Public/Localization/V1): versioned locale list and language-pack parameters/results;
  there is no parallel consumer catalog interface.
- [UiLocaleFields.cs](../../src/Jularr.Backend/Data/Fields/Localization/UiLocaleFields.cs),
  [ProviderFields.cs](../../src/Jularr.Backend/Data/Fields/Provider/ProviderFields.cs): required locale/provider catalog fields.

## Frontend

- [UiText.cs](../../src/Jularr.Frontend/Framework/UiText.cs), [UiResources.cs](../../src/Jularr.Frontend/Localization/UiResources.cs): service-backed
  request language pack and English registration inputs; [Catalogs](../../src/Jularr.Frontend/Localization/Catalogs) hold the shipped languages
  (`{key: [English, text]}`).
- [Localization](../../src/Jularr.Frontend/Pages/Admin/Localization/Localization.cshtml.cs),
  [Languages](../../src/Jularr.Frontend/Pages/Admin/Languages/Languages.cshtml.cs): DB descriptions, translations and locale administration.

## Tests

- Shared localization, startup idempotence, manual protection, fallback, cache invalidation and rollback: `UiLocalizationTests.cs`.
