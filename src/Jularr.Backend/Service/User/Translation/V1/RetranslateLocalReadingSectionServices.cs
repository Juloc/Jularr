using System.Net.Http;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data;
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
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(20);
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

            // The blocks with text of their own; a block holding others, or without text, has nothing to translate, and an oversized one keeps its text.
            var pending = new List<(int Index, string Text)>();
            for (var index = 0; index < nodes.Length; index++)
            {
                var source = ReadingHtml.TranslationText(nodes[index]);
                if (nodes[index].QuerySelector("[data-b]") is null && source.Length is > 0 and <= 20_000) pending.Add((index, source));
            }

            if (pending.Count == 0) return new LocalReadingRetranslationV1(0, 0);
            var machine = await _providers.ChooseAsync(setup, CancellationToken, Parameters.Model) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            string digest;
            try { digest = await machine.Provider.DigestAsync(machine, CancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
            { throw new ServiceUnavailableException("translation_local_unavailable", exception); }

            var done = new Dictionary<int, string>();
            var failed = false;
            var batch = machine.Provider as IBatchTranslationProvider;
            for (var start = 0; start < pending.Count && !failed;)
            {
                // One request takes as many blocks as the provider allows; a provider without batches is asked block by block.
                var take = 1;
                var characters = pending[start].Text.Length;
                while (batch is not null && start + take < pending.Count && characters + pending[start + take].Text.Length <= batch.BatchCharacters) characters += pending[start + take++].Text.Length;
                var group = pending.GetRange(start, take);
                start += take;

                if (!await _gate.HeavyWork.WaitAsync(GateWait, CancellationToken)) throw new ServiceUnavailableException("translation_local_busy");
                try
                {
                    var context = ReadingHtml.TranslationContext(nodes, group[0].Index);
                    var answers = batch is not null ? await batch.TranslateBlocksAsync(machine, Data.SourceLanguage, Parameters.Language, [.. group.Select(item => item.Text)], context, CancellationToken)
                        : [await machine.Provider.TranslateBlockAsync(machine, Data.SourceLanguage, Parameters.Language, group[0].Text, context, CancellationToken)];
                    for (var k = 0; k < group.Count; k++)
                    {
                        var answer = answers[k] ?? (batch is null ? null : await machine.Provider.TranslateBlockAsync(machine, Data.SourceLanguage, Parameters.Language, group[k].Text,
                            ReadingHtml.TranslationContext(nodes, group[k].Index), CancellationToken));
                        if (answer is not null) done[group[k].Index] = answer;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    machine.Provider.Fail(machine);
                    failed = true;
                }
                finally { _gate.HeavyWork.Release(); }
            }

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
}
