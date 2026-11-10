# AI: Codex connection

The instance Codex (ChatGPT) connection, its settings and Codex translation.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- Codex connection and settings (admin-only, one ChatGPT account for the instance; no module; the Owner's choices are `InstanceSettings.Codex*`,
  migration `0038_codex_settings`): [CodexCli.cs](../../src/Jularr.Integrations/Logic/Codex/CodexCli.cs) runs the image's `codex` (version,
  `login status`, `logout`, `login --device-auth` whose address from OpenAI's host and one-time code it reads; `CODEX_HOME` is `<DataPath>/codex`,
  `Ai:CodexPath`/`Ai:CodexHome` override; credential files are never read, only fixed state words leave),
  [CodexConnection.cs](../../src/Jularr.Backend/Service/Core/CodexConnection.cs) holds the one pending login in memory,
  [CodexServices.cs](../../src/Jularr.Backend/Service/Admin/Ai/V1/CodexServices.cs) (`ReadCodexConnectionV1`, `StartCodexLoginV1`,
  `CancelCodexLoginV1`, `LogoutCodexV1`, right `instance.settings.read`/`update`);
  [CodexAppServer.cs](../../src/Jularr.Integrations/Logic/Codex/CodexAppServer.cs) asks a short `codex app-server` session for the account's models
  (`model/list`) and plan usage (`account/rateLimits/read`), `CodexConnection.ReadCatalogAsync` caches it a minute, `ReadCodexCatalogV1` and
  `ReadCodexSettingsV1`/`SaveCodexSettingsV1` (model, effort, fallback model, daily and per-run token limits, warn percent; `CodexSettings.ReadAsync`
  is what translation reads); tests `CodexConnectionTests` with a stand-in CLI that also answers the app-server.
  `IBatchTranslationProvider` (several numbered blocks per request; the book job's `TranslateBatchAsync` uses it, a failed block is asked alone),
  `CodexConnection` keeps the plan limit (`PlanLimitedUntil`, from Codex's own `turn.failed` words, rests Codex at most a day), the last 20 requests
  and the tokens per day (`<CODEX_HOME>/jularr-usage.json`), `ReadCodexActivityV1`.
  `RetranslateLocalReadingSectionV1` (Owner only, `instance.settings.update`) re-translates a chapter with a named model in whole-passage requests,
  replaces that model's cache of the section (`StoreAsync(..., replace: true)`) and records the blocks in `ReadingTranslationChoices` so every reader
  sees them; the Reader's one re-translation offers the account's Codex models (the AI page choice first) in its model list to holders of
  `instance.settings.update` and runs its chapter or whole book through this service (`Modules/Reader/Translation/codex.ts`, chapter after chapter
  with Stop, ending at the first 503, then a reload), its paragraph block by block; tests `BookTranslationTests.Retranslate_*`, `ReaderCodexTests`.
  Codex never translates by itself: `TranslationProviders.ChooseForAsync` (`ReadLocalTranslationAvailabilityV1`: `Model` of a local machine only,
  `Away`, and for `instance.settings.update` holders `Codex`, the model a button press translates with, and `CodexProblem` (`limit`, `unavailable`;
  nothing while Codex is not connected) from `IBatchTranslationProvider.OfferAsync`) leaves the Reader in the original text and the bar under it
  (`Modules/Reader/Translation/CodexBar.ts`: Translate with Codex, the next chapter's button, Stop, Try again, the time left) offers the button.
  A press translates the chapter in chunks (`TranslateLocalReadingPassagesV1`, one request per call: the missing blocks from the block in view onward,
  then round to the start, at most 100 blocks / 12,000 characters, `BatchBlocks`/`BatchCharacters` of the provider;
  `PassageTranslation.Chunk`/`TranslateChunkAsync`, no choices).
  A chunk is one streamed request in a bare app-server thread (`CodexCli.StreamAsync`, `CodexAppServer.StreamAsync`: a one-line instruction, tools,
  plugins and environment notes switched off, so a request costs ~3k tokens besides its text instead of ~14k with `exec`;
  `CodexTranslationProvider.TranslateBlocksStreamAsync` cuts the answer into blocks with `NumberedStream`); the service stores each block as it is
  complete (every 1.5 s), the lane (`LocalTranslationGate.HeavyWork`) is held per request, `CodexChapter.ts` loops until nothing remains, fetches the
  stored blocks while a request runs and shows them, estimates the time left from how fast the text comes, Stop drops the request (and so Codex),
  leaves out blocks whose answers failed the checks, waits on a busy lane and names a failure (`translation_limit_reached`,
  `translation_not_connected`, `translation_local_busy`, `translation_request_failed`) while keeping the finished chunks; what Codex translated stays
  readable while it is away.
  `Away` when the Ollama machines set up all rest (`LocalTranslationMachines.AllResting`), shown as `reader.language.away`.
  Each chapter counts its requests together, also the chunks of one chapter within a quarter of an hour (`IBatchTranslationProvider.BeginChapter`,
  `CodexConnection.BeginRun`, the last 20 in `CodexActivityV1.Chapters` with model, effort, requests, tokens and seconds on the AI page); tests
  `ReadingRetranslationTests.Codex_*`/`Chunk*`, `CodexTranslationTests.Runs_*`, `Tools/codex-chapter.test.mjs`,
  `ReaderCodexTests.Owner_Without*`/`Owner_WhenCodexFails*`.

## Frontend

- [AI](../../src/Jularr.Frontend/Pages/Admin/Ai/Ai.cshtml.cs) (`/Admin/Ai`, Connections group, Admin right): one card for the instance's Codex
  connection (state chip, version and sign-in, Connect, the address and one-time code while a login waits with Cancel, Disconnect as a confirmation);
  once connected, a Plan usage card (the ChatGPT plan's windows as bars, [Ai.entry.scss](../../src/Jularr.Frontend/Pages/Admin/Ai/Ai.entry.scss)) and
  an Activity card (the running and last requests, asked again every three seconds while one runs), tokens per day and the plan limit on the usage
  card, and a Settings card (model and fallback from the account's live list, effort, token limits; saved with `?handler=Settings`);
  [Ai.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Ai/Ai.entry.ts) asks `?handler=Card` every three seconds while a login waits and reloads the
  page when its state changed.
