# Translation and speech

Server translation (Ollama models), book translation ahead of reading and speech models.
Index: [CODEMAP](../CODEMAP.md).
Specs: [SPEECH](../SPEECH.md).

## Backend

- Optional server translation: [Ollama API adapter](../../src/Jularr.Integrations/Logic/Translation/OllamaLocalModels.cs), [Reader block and
  provider-specific cache](../../src/Jularr.Backend/Service/User/Translation/V1/LocalReadingTranslationServices.cs), [admin model/pull/benchmark
  Services](../../src/Jularr.Backend/Service/Admin/Translation/V1/LocalTranslationModelServices.cs), [small-image model-worker and verified
  budget](../../src/Jularr.Backend/Service/Core/LocalTranslationOptions.cs); several Ollama machines in the Owner's order
  (`InstanceSettings.TranslationMachines`, migration `0030_translation_machines`, empty means `LocalTranslation:Endpoint` alone):
  `LocalTranslationMachines` gives every translation (Reader, `bookTranslations` job, subtitles, benchmark) the first machine that answers with the
  model, and a machine that fails rests a minute so the next takes over (a machine that takes the connection but never answers fails too:
  `OllamaLocalModels` ends a question after 15 s and a translation after 8 minutes, not the client's 65); smoke and Reader API tests
  `LocalTranslationModelTests`, `LocalReadingTranslationIntegrationTests`.
- Translation provider seam: [TranslationProviders.cs](../../src/Jularr.Backend/Service/Core/TranslationProviders.cs) (`ITranslationProvider`: `Key`,
  `Models`, `IsAdminOnly`, `AvailableAsync`, `ChooseAsync`, `DigestAsync`, `TranslateBlockAsync`, `TranslateAsync`, `Fail`; registry
  `TranslationProviders` resolves ids `<provider>:<model>` such as `ollama:translategemma:12b`, lists the models a caller may choose now
  (`AvailableAsync`) and checks a choice (`MayChoose`: an admin-only provider such as Codex only with `instance.settings.update`, the right of
  `RetranslateLocalReadingSectionV1`, except the Owner's default model; `Passages` marks a model whose chapters go in whole-passage requests); every
  server translation (Reader blocks, `bookTranslations` job, subtitles) goes through it, and a new provider only registers as `ITranslationProvider`
  in `ServiceApi`. Ollama's caches keep their key `:localtranslation/v4/<model>/…`, other providers use their whole id (`LocalReadingPaths.ModelKey`).
  A new model digest keeps the earlier texts as `Stale` in the cache, shown marked until the job (missing sections first) has replaced them and never
  counted as finished; `ReadingTranslationChoices` (migration `0039_translation_variants`) names the model a block shows for every reader: the Reader
  re-translates a paragraph, a chapter or the whole book with a chosen model ([ReadReaderTranslationModelsV1,
  ChooseLocalReadingTranslationV1](../../src/Jularr.Backend/Service/User/Translation/V1/LocalReadingTranslationServices.cs),
  [ReadingTranslationChoiceLogic.cs](../../src/Jularr.Backend/Logic/Reading/ReadingTranslationChoiceLogic.cs)), the choice is kept first and a block
  keeps its earlier text until the chosen model has translated it (the Reader asks for the paragraph and the chapter's blocks at once,
  `bookTranslations` translates the other chosen blocks with their model before the default pass); the Owner's default model is
  `InstanceSettings.TranslationModel` (Setup tab, `SaveTranslationModelV1`; empty: largest installed TranslateGemma). Tests
  `LocalTranslationModelTests.Providers_*`, `BookTranslationTests.Job_ANewModelVersion_*`, `LocalReadingTranslationIntegrationTests.DefaultModel_*`,
  `ReadingRetranslationTests`, E2E `ReaderTranslationTests.AnotherModel_*`.
- Speech models: [catalog](../../src/Jularr.Backend/Logic/Speech/SpeechCatalog.cs), [metadata](../../src/Jularr.Backend/Logic/Speech/models.json),
  [activation/search/explicit refresh](../../src/Jularr.Backend/Service/Admin/Speech/V1/SpeechModelServices.cs), [official discovery
  sources](../../src/Jularr.Integrations/Logic/Speech/SpeechCatalogClient.cs), [device
  delivery](../../src/Jularr.Backend/Service/User/Speech/V1/SpeechModelServices.cs), [files and persisted
  discovery](../../src/Jularr.Backend/Service/Core/SpeechFiles.cs), [verified
  download](../../src/Jularr.Integrations/Logic/Speech/SpeechModelClient.cs).
  Activation state is in `Instance.sql`; [scope and open work](../SPEECH.md), tests `SpeechTests` / `SpeechDiscoveryTests` / `SpeechPathTests`.
- Translation (module `Translation`, off by default; files in `DataPath/translation`): the reader opens a book in the profile's language unless the
  profile chose another or the original for it (language button in the reader's top bar, kept per profile and Work in `ReadingLanguages` through
  `SaveReadingLanguageV1`), and shows the server's TranslateGemma translation (the Reader asks Mozilla for nothing unless the Mozilla module is on and
  the server could not translate; the browser then translates the page in view and the three after it, moving along with the reader, keeps its results
  only in the browser and neither reads nor writes the server's section cache): it can translate in the browser between every language Mozilla
  publishes models for (`ReadTranslationLanguagesV1`; other pairs through English; the source is the book's language tag, else the Work's original
  language, else its text) with Mozilla's Bergamot engine (WASM 3.0) and its first-collection models; other pairs go through English
  ([TranslationModels.cs](../../src/Jularr.Backend/Logic/Translation/TranslationModels.cs): newest released model for every build, engine major
  version, route).
  [TranslationFiles.cs](../../src/Jularr.Backend/Service/Core/TranslationFiles.cs) lists them through
  [MozillaTranslationsClient.cs](../../src/Jularr.Integrations/Logic/Translation/MozillaTranslationsClient.cs) (Remote Settings, attachments checked
  against size and SHA-256) and fetches each file once when a reader first needs it;
  [TranslationServices.cs](../../src/Jularr.Backend/Service/User/Translation/V1/TranslationServices.cs) serves the route and the files (immutable by
  hash).
  The server keeps nothing of the browser's translation: the earlier per-section rows `:translation/<language>/<NNNNN>` and their endpoints are gone
  (migration [0041](../../database/migrations/0041_remove_browser_translation_rows.sql) deleted the rows after the pre-update backup); tests
  `TranslationTests`, `DatabaseMigrationTests.BrowserTranslationRows_*`.
- Book translations ahead of reading (module `Translation`, a TranslateGemma model installed):
  [TranslationWorker.cs](../../src/Jularr.Workers/TranslationWorker.cs) runs
  [TranslateReadingBooksV1](../../src/Jularr.Backend/Service/System/Reading/V1/BookTranslationServices.cs) a few blocks per call into the Reader's
  local-translation cache (`LocalReadingPaths`), for every language a profile uses (`Profiles.UiLocaleId`, `ReadingPreferences.Language`), books being
  read first from their chapter (`MediaProgress`); a reader's own request comes first (`LocalTranslationGate`).
  The Translation page's tab "Translated works" lists every book per language and model (`ReadTranslatedBooksV1`, `/api/v1/admin/translation/books`,
  admin-only: sections translated by the same mixing as the EPUB, per model its sections done and those still in an earlier version, the EPUB once
  complete); the work page's Translations list and its delete use the same caches; the delete asks first in a dialog naming the language and the
  models (`DeleteTranslationDataV1.Confirmed`, refused without it).
  Tests: [BookTranslationTests.cs](../../tests/Jularr.Backend.Tests/BookTranslationTests.cs).

## Frontend

- [Device speech](../../src/Jularr.Frontend/Modules/Speech/DeviceSession.ts), [optional neural
  session](../../src/Jularr.Frontend/Modules/Speech/NeuralSession.ts), [verified device
  models](../../src/Jularr.Frontend/Modules/Speech/ModelStore.ts), [shared profile/device
  controls](../../src/Jularr.Frontend/Modules/Speech/SpeechSettings.cs), [Reader integration](../../src/Jularr.Frontend/Modules/Reader/Speech.ts).
  [Runtime sources/notices](../../src/Jularr.Frontend/Modules/Speech/Runtime/NOTICE.md), [scope/gaps](../SPEECH.md); focused tests
  `Tools/speech.test.mjs`, `Tools/neural-speech.test.mjs`, `SpeechTests`, `SpeechPathTests`; real-model replay `Tools/speech-neural-smoke.mjs`.
- [Local translation models](../../src/Jularr.Frontend/Pages/Admin/Translation/Translation.cshtml.cs) (a column of section cards; tabs Progress,
  Translated works with the translated EPUB per book and language, Setup; E2E `TranslationPageTests`): the Ollama machines in order (one address per
  line, each a card with its state, whether it is reachable, what it answers with, its last error with the reason and time, until when it rests, and a
  download per missing model), Owner-only model inventory, explicit external Ollama pull and short timing smoke with an operator-verified
  runtime-volume budget; [Reader](../../src/Jularr.Frontend/Modules/Reader/reader.entry.ts) reuses canonical blocks with a separately versioned
  TranslateGemma cache, its only translator (the `bookTranslations` job fills it ahead).
  Backend Service/Data/Logic adapters live at
  [LocalTranslationModelServices.cs](../../src/Jularr.Backend/Service/Admin/Translation/V1/LocalTranslationModelServices.cs).
- [Speech models](../../src/Jularr.Frontend/Pages/Admin/Speech/Speech.cshtml.cs): `/Admin/Instance/Speech`, reached from module administration; shared
  list/drawer/Form, paged search, explicit source refresh; activation (and accepting the model's conditions) is a switch in the model's row saved by
  the pill, the drawer only shows details.
  [Sample playback](../../src/Jularr.Frontend/Pages/Admin/Speech/Speech.entry.ts) loads one official prerecorded clip only on Play; no synthesis or
  weights.
  [Speech scope](../SPEECH.md); tests `SpeechTests`, `SpeechDiscoveryTests`, `SpeechPathTests`.
