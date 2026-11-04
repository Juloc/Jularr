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

    // The default translation of a model that translates whole passages (Codex): the section's blocks its cache lacks in this version, in whole-passage requests
    // instead of a request per block. Stored like any default translation, no choice for other readers; a block whose answer fails the checks stays missing.
    [ServiceEndpoint("POST", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/passages")]
    public sealed class TranslateLocalReadingPassagesV1 : UserService<LocalReadingSectionModelParametersV1, LocalReadingBlockDataV1>
    {
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
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingRetranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingSectionModelParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingSectionModelParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_providers.Find(Parameters.Model) is not ({ } provider, _)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            if (provider is not IBatchTranslationProvider) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            if (!_providers.MayChoose(Parameters.Model, setup, Auth))
                throw new SecurityException("Only the admin-only right may translate with this model.");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var nodes = new HtmlParser().ParseDocument(section.Html).QuerySelectorAll("[data-b]").ToArray();
            if (nodes.Length != section.Blocks) throw new DataValidationException(nameof(Parameters.SectionIndex), "translation_mismatch");

            // Only what this version of the model has not translated yet.
            var (machine, digest) = await PassageTranslation.ChooseAsync(_providers, setup, Parameters.Model, CancellationToken);
            var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);
            var cached = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var row = await ctx.ReadOptionalAsync<ReadingTranslationCacheData>(LocalReadingPaths.Read(Parameters.DocumentId, path), ct);
                return row is null ? null : LocalReadingPaths.Parse(row.Cache, section.Html, Data.SourceLanguage, digest, nodes.Length);
            }, CancellationToken);
            var pending = PassageTranslation.Pending(nodes, index => string.IsNullOrWhiteSpace(cached?.Texts[index]));
            if (pending.Count == 0) return new LocalReadingRetranslationV1(0, 0);

            var done = await PassageTranslation.TranslateAsync(machine, section, nodes, pending, Data.SourceLanguage, Parameters.Language, _gate, CancellationToken);
            if (done.Count == 0) throw new ServiceUnavailableException("translation_local_unavailable");
            await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, Parameters.DocumentId, path, section.Html, Data.SourceLanguage, digest, nodes.Length, done, ct), CancellationToken);
            return new LocalReadingRetranslationV1(done.Count, pending.Count - done.Count);
        }
    }

    // A section's blocks in whole-passage requests, shared by the Owner's re-translation and the default translation of a passages model.
    internal static class PassageTranslation
    {
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(20);

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
                while (batch is not null && start + take < pending.Count && characters + pending[start + take].Text.Length <= batch.BatchCharacters) characters += pending[start + take++].Text.Length;
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
