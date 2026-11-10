# Transcoding and subtitles

Transcoding limits, benchmark and priority, and subtitle providers.
Index: [CODEMAP](../CODEMAP.md).
Specs: [PARITY_BAZARR](../PARITY_BAZARR.md).

## Backend

- Subtitles (module `Subtitles`): providers are acquisition connections of the subtitle role (OpenSubtitles.com with API key and account, SubDL with
  API key; [SubtitleProviders.cs](../../src/Jularr.Backend/Service/Core/SubtitleProviders.cs) over the clients in
  [Logic/Subtitles](../../src/Jularr.Integrations/Logic/Subtitles)); the Admin orders wanted languages on the instance page;
  [FetchSubtitlesV1](../../src/Jularr.Backend/Service/System/Media/V1/SubtitleServices.cs) takes one identified video at a time
  ([SubtitleWorker.cs](../../src/Jularr.Workers/SubtitleWorker.cs)), skips languages it has embedded or beside it
  ([LanguageCodes.cs](../../src/Jularr.Backend/Logic/Media/LanguageCodes.cs)), prefers the same release, saves `<video>.<language>.srt` for the
  video's asset and looks again after a day; [SubtitleSearchServices.cs](../../src/Jularr.Backend/Service/Admin/Library/V1/SubtitleSearchServices.cs)
  lets an administrator search one video's subtitles in a language and choose one; both share
  [SubtitleFiles.cs](../../src/Jularr.Backend/Service/Core/SubtitleFiles.cs); test `SubtitleTests`.

## Frontend

- [Transcoding](../../src/Jularr.Frontend/Pages/Admin/Transcoding/Transcoding.cshtml.cs) (`/Admin/Transcoding`, the Jobs entry): the machine's
  encoders with the last benchmark's speeds and proposal, Run benchmark (the page reads itself again while it runs,
  [client](../../src/Jularr.Frontend/Pages/Admin/Transcoding/Transcoding.entry.ts)) and the editable limits with the save pill; `TranscodeTests`, E2E
  `TranscodingTests`.

## Tests

- Transcoding limits, benchmark and priority ([Playback.sql](../../database/Playback.sql) `TranscodeSettings`, `TranscodeBenchmarks`;
  `Accounts.TranscodingPriority`): [TranscodingServices.cs](../../src/Jularr.Backend/Service/Admin/Transcoding/V1/TranscodingServices.cs) reads and
  saves the Owner's acceleration choice (Auto follows the benchmark, else the `Media:*` arguments), the maximum of conversions at once (counted in
  1080p units, `Media:MaxTranscodes` is the default) and the ladder of lower picture heights, and queues the benchmark as an operation
  (`OperationType.TranscodeBenchmark`, run only on demand by
  [ExecuteTranscodeBenchmarkV1](../../src/Jularr.Backend/Service/System/Transcoding/V1/TranscodeBenchmarkServices.cs) in the
  [OperationWorker](../../src/Jularr.Workers/OperationWorker.cs)).
  [TranscodeBenchmarkClient.cs](../../src/Jularr.Integrations/Logic/Media/TranscodeBenchmarkClient.cs) reads `-hwaccels` / `-encoders` and render
  nodes (cached by [TranscodeHardware.cs](../../src/Jularr.Backend/Service/Core/TranscodeHardware.cs)) and times short real encodes of a 1080p and a
  4K-to-1080p clip through every available pipeline in [TranscodePipelines.cs](../../src/Jularr.Integrations/Logic/Media/TranscodePipelines.cs), the
  same arguments playback uses; [TranscodeLogic.cs](../../src/Jularr.Backend/Logic/Playback/TranscodeLogic.cs) proposes the fastest pipeline and a
  conservative count, resolves a plan's encoding and builds the ladder candidates.
  [TranscodeAdmissionLogic.cs](../../src/Jularr.Backend/Logic/Playback/TranscodeAdmissionLogic.cs) is the one rule: re-encoded video weighs by its
  picture height, copying and direct play nothing; the wanted conversion, else the first lighter ladder step that fits, else
  `playback_capacity_exhausted`; an account with `TranscodingPriority` first stops the newest conversions of accounts without it (never another
  priority account's), their players get `playback_evicted` (HTTP 410) on the next request.
  `StartPlaybackV1` asks `PlaybackTranscodes.Admit` before the session exists and marks a lighter plan `IsReduced`; the player shows both messages.
  Tests `TranscodeTests`, E2E `TranscodingTests`, `TracksTests`.
