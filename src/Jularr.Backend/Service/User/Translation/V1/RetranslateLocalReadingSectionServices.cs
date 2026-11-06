using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;

namespace Jularr.Backend.Service.User.Translation.V1
{
    // The Owner re-translates one chapter by hand with a named model (Codex), in requests of whole passages. The new texts replace that model's cache of the section and are recorded in
    // ReadingTranslationChoices, so every reader of the book sees them whichever model translates by default; a block whose answer fails the checks keeps what it showed.
    [ServiceEndpoint("POST", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/retranslate")]
    public sealed class RetranslateLocalReadingSectionV1 : UserService<LocalReadingSectionModelParametersV1, LocalReadingBlockDataV1>
    {
        private readonly ReadReadingSectionV1 _sections;
        private readonly TranslationProviders _providers;
        private readonly LocalTranslationGate _gate;

        internal RetranslateLocalReadingSectionV1(ReadReadingSectionV1 sections, TranslationProviders providers, LocalTranslationGate gate)
        {
            _sections = sections;
            _providers = providers;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingRetranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingSectionModelParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingSectionModelParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_providers.Find(Parameters.Model) is null) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var nodes = new HtmlParser().ParseDocument(section.Html).QuerySelectorAll("[data-b]").ToArray();
            if (nodes.Length != section.Blocks) throw new DataValidationException(nameof(Parameters.SectionIndex), "translation_mismatch");

            var pending = PassageTranslation.Pending(nodes, _ => true);
            if (pending.Count == 0) return new LocalReadingRetranslationV1(0, 0);
            var (machine, digest) = await PassageTranslation.ChooseAsync(_providers, setup, Parameters.Model, CancellationToken);
            var done = await PassageTranslation.TranslateAsync(machine, section, nodes, pending, Data.SourceLanguage, Parameters.Language, _gate, CancellationToken);

            // Nothing translated at all: the Owner is told why, and nothing was changed.
            if (done.Count == 0) throw new ServiceUnavailableException("translation_local_unavailable");

            var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);
            var blocks = JsonSerializer.Serialize(done.Keys.Order());
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await LocalReadingPaths.StoreAsync(ctx, Parameters.DocumentId, path, section.Html, Data.SourceLanguage, digest, nodes.Length, done, ct, replace: true);
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                    SELECT @DocumentId, @SectionIndex, @Language, block::integer, @Model FROM jsonb_array_elements_text(@Blocks::jsonb) AS block
                    ON CONFLICT ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex") DO UPDATE SET "Model" = EXCLUDED."Model", "ChosenAt" = now();
                    """;
                sql.Parameters.Add(new ReadingTranslationChoiceWriteData(Parameters.DocumentId, Parameters.SectionIndex, Parameters.Language, Parameters.Model, blocks));
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                return 0;
            }, CancellationToken);
            return new LocalReadingRetranslationV1(done.Count, pending.Count - done.Count);
        }
    }

    // The default translation of a model that translates in several requests (Codex), one request per call: the next stretch of blocks this version of the model has not
    // translated yet, from the block in view onward and round to the start. The Reader asks again until nothing remains and shows each answer as it arrives, so a reader
    // never waits for the chapter and the single translation lane is held for one request only. Stored like any default translation, no choice for other readers.
    [ServiceEndpoint("POST", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/passages")]
    public sealed class TranslateLocalReadingPassagesV1 : UserService<LocalReadingSectionModelParametersV1, LocalReadingChunkDataV1>
    {
        private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(1.5);
        private readonly ReadReadingSectionV1 _sections;
        private readonly TranslationProviders _providers;
        private readonly LocalTranslationGate _gate;

        internal TranslateLocalReadingPassagesV1(ReadReadingSectionV1 sections, TranslationProviders providers, LocalTranslationGate gate)
        {
            _sections = sections;
            _providers = providers;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingChunkV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingSectionModelParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingSectionModelParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_providers.Find(Parameters.Model) is not ({ } provider, _)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            if (provider is not IBatchTranslationProvider batch) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            if (!_providers.MayChoose(Parameters.Model, setup, Auth))
                throw new SecurityException("Only the admin-only right may translate with this model.");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var nodes = new HtmlParser().ParseDocument(section.Html).QuerySelectorAll("[data-b]").ToArray();
            if (nodes.Length != section.Blocks) throw new DataValidationException(nameof(Parameters.SectionIndex), "translation_mismatch");

            // Only what this version of the model has not translated yet.
            TranslationMachine machine;
            string digest;
            try { (machine, digest) = await PassageTranslation.ChooseAsync(_providers, setup, Parameters.Model, CancellationToken); }
            catch (ServiceUnavailableException exception) { throw new ServiceUnavailableException(await PassageTranslation.ProblemAsync(batch, setup, CancellationToken), exception); }
            var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);
            var cached = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var row = await ctx.ReadOptionalAsync<ReadingTranslationCacheData>(LocalReadingPaths.Read(Parameters.DocumentId, path), ct);
                return row is null ? null : LocalReadingPaths.Parse(row.Cache, section.Html, Data.SourceLanguage, digest, nodes.Length);
            }, CancellationToken);
            var pending = PassageTranslation.Pending(nodes, index => string.IsNullOrWhiteSpace(cached?.Texts[index]) && !Data.Skip.Contains(index));
            if (pending.Count == 0) return new LocalReadingChunkV1([], [], 0, 0, 0, 0);

            // One request, one hold of the lane. Its blocks are stored while the answer streams in, so the Reader (asking for the section's texts meanwhile) shows each as
            // it is complete, and what was done stays when the request fails.
            var chunk = PassageTranslation.Chunk(pending, Data.From, batch);
            if (!await _gate.HeavyWork.WaitAsync(_gate.ChunkWait, CancellationToken)) throw new ServiceUnavailableException("translation_local_busy");
            var done = new ConcurrentDictionary<int, string>();
            var waiting = new ConcurrentQueue<KeyValuePair<int, string>>();
            async Task SaveAsync()
            {
                var blocks = new Dictionary<int, string>();
                while (waiting.TryDequeue(out var item)) blocks[item.Key] = item.Value;
                if (blocks.Count > 0)
                    await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, Parameters.DocumentId, path, section.Html, Data.SourceLanguage, digest, nodes.Length, blocks, ct), CancellationToken);
            }

            using var saving = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            var saver = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(SaveEvery);
                try
                {
                    while (await timer.WaitForNextTickAsync(saving.Token)) await SaveAsync();
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken);
            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try
            {
                using var run = batch.BeginChapter(machine, section.SectionIndex + 1, section.Title);
                await PassageTranslation.TranslateChunkAsync(machine, nodes, chunk, Data.SourceLanguage, Parameters.Language, (index, text) =>
                {
                    if (done.TryAdd(index, text)) waiting.Enqueue(new KeyValuePair<int, string>(index, text));
                }, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                failure = exception;
            }
            finally
            {
                await saving.CancelAsync();
                await saver;
                _gate.HeavyWork.Release();
            }
            var seconds = Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds, 1);

            await SaveAsync();
            if (failure is not null) throw new ServiceUnavailableException(await PassageTranslation.ProblemAsync(batch, setup, CancellationToken), failure);
            var failed = chunk.Where(item => !done.ContainsKey(item.Index)).Select(item => item.Index).ToArray();
            var left = pending.Except(chunk).ToList();
            return new LocalReadingChunkV1([.. done.OrderBy(item => item.Key).Select(item => new LocalReadingChunkBlockV1(item.Key, item.Value))], failed,
                chunk.Sum(item => item.Text.Length), seconds, left.Count, left.Sum(item => item.Text.Length));
        }
    }

    // A section's blocks in whole-passage requests, shared by the Owner's re-translation and the default translation of a passages model.
    internal static class PassageTranslation
    {
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(20);
        private const int AskedAlone = 5;

        // The wanted blocks with text of their own; a block holding others, or without text, has nothing to translate, and an oversized one keeps its text.
        public static List<(int Index, string Text)> Pending(IElement[] nodes, Func<int, bool> wanted)
        {
            var pending = new List<(int Index, string Text)>();
            for (var index = 0; index < nodes.Length; index++)
            {
                var source = ReadingHtml.TranslationText(nodes[index]);
                if (wanted(index) && nodes[index].QuerySelector("[data-b]") is null && source.Length is > 0 and <= 20_000) pending.Add((index, source));
            }

            return pending;
        }

        // The machine of the named model and the version of its model; unavailable when none answers.
        public static async Task<(TranslationMachine Machine, string Digest)> ChooseAsync(TranslationProviders providers, TranslationSetupV1 setup, string model, CancellationToken ct)
        {
            var machine = await providers.ChooseAsync(setup, ct, model) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            try { return (machine, await machine.Provider.DigestAsync(machine, ct)); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new ServiceUnavailableException("translation_local_unavailable", exception); }
        }

        // The next request's blocks: the missing ones from `from` onward, then round to the start, as many as the provider takes in one request.
        public static List<(int Index, string Text)> Chunk(List<(int Index, string Text)> pending, int from, IBatchTranslationProvider batch)
        {
            var chunk = new List<(int Index, string Text)>();
            var characters = 0;
            foreach (var item in pending.Where(item => item.Index >= from).Concat(pending.Where(item => item.Index < from)))
            {
                if (chunk.Count > 0 && (chunk.Count >= batch.BatchBlocks || characters + item.Text.Length > batch.BatchCharacters)) break;
                chunk.Add(item);
                characters += item.Text.Length;
            }

            return chunk;
        }

        // The chunk in one streamed request with the passage before it, each block handed to `arrived` as it is complete. A block whose answer fails the checks is asked
        // alone, a few at most (a request that fails most blocks is broken, not unlucky); a failure of those asks keeps what came, and the blocks left stay missing.
        public static async Task TranslateChunkAsync(TranslationMachine machine, IElement[] nodes, List<(int Index, string Text)> chunk, string source, string target, Action<int, string> arrived,
            CancellationToken ct)
        {
            var batch = (IBatchTranslationProvider)machine.Provider;
            var answers = await batch.TranslateBlocksStreamAsync(machine, source, target, [.. chunk.Select(item => item.Text)], ReadingHtml.TranslationContext(nodes, chunk[0].Index),
                (position, text) => arrived(chunk[position].Index, text), ct);
            var missing = chunk.Where((item, position) => answers[position] is null).Take(AskedAlone).ToList();
            try
            {
                foreach (var item in missing)
                {
                    if (await machine.Provider.TranslateBlockAsync(machine, source, target, item.Text, ReadingHtml.TranslationContext(nodes, item.Index), ct) is { } alone) arrived(item.Index, alone);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }

        // Why a request failed, as the error code the Reader names to the person: the provider's own state (limit, not signed in), else a failed request.
        public static async Task<string> ProblemAsync(IBatchTranslationProvider provider, TranslationSetupV1 setup, CancellationToken ct) =>
            (await provider.OfferAsync(setup, ct)).Problem switch
            {
                "limit" => "translation_limit_reached",
                "not_connected" => "translation_not_connected",
                _ => "translation_request_failed"
            };

        // The translated blocks by index. One request takes as many blocks as the provider allows (a provider without batches is asked block by block); a block
        // whose batch answer fails the checks is asked alone. A failing machine rests and ends the run with what was translated so far.
        public static async Task<Dictionary<int, string>> TranslateAsync(TranslationMachine machine, ReadingSectionV1 section, IElement[] nodes, List<(int Index, string Text)> pending,
            string source, string target, LocalTranslationGate gate, CancellationToken ct)
        {
            var done = new Dictionary<int, string>();
            var batch = machine.Provider as IBatchTranslationProvider;
            using var chapter = batch?.BeginChapter(machine, section.SectionIndex + 1, section.Title);
            for (var start = 0; start < pending.Count;)
            {
                var take = 1;
                var characters = pending[start].Text.Length;
                while (batch is not null && start + take < pending.Count && take < batch.BatchBlocks && characters + pending[start + take].Text.Length <= batch.BatchCharacters)
                    characters += pending[start + take++].Text.Length;
                var group = pending.GetRange(start, take);
                start += take;

                if (!await gate.HeavyWork.WaitAsync(GateWait, ct)) throw new ServiceUnavailableException("translation_local_busy");
                try
                {
                    var context = ReadingHtml.TranslationContext(nodes, group[0].Index);
                    var answers = batch is not null ? await batch.TranslateBlocksAsync(machine, source, target, [.. group.Select(item => item.Text)], context, ct)
                        : [await machine.Provider.TranslateBlockAsync(machine, source, target, group[0].Text, context, ct)];
                    for (var k = 0; k < group.Count; k++)
                    {
                        var answer = answers[k] ?? (batch is null ? null : await machine.Provider.TranslateBlockAsync(machine, source, target, group[k].Text,
                            ReadingHtml.TranslationContext(nodes, group[k].Index), ct));
                        if (answer is not null) done[group[k].Index] = answer;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    machine.Provider.Fail(machine);
                    break;
                }
                finally { gate.HeavyWork.Release(); }
            }

            return done;
        }
    }
}
