using System.Net.Http;
using System.Security;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Translation.V1
{
    // Provider-specific cache: intentionally outside the canonical :translation/{language} paths.
    internal static class LocalReadingPaths
    {
        public const int Algorithm = 4;

        // Ollama's caches keep the key they had before model ids named their provider ("translategemma:12b"), so nothing is translated again;
        // every other provider's key is its whole model id.
        public static string ModelKey(string modelId) =>
            modelId.StartsWith(OllamaTranslationProvider.ProviderKey + ":", StringComparison.Ordinal) ? modelId[(OllamaTranslationProvider.ProviderKey.Length + 1)..] : modelId;

        public static string Cache(int section, string language, string modelId) =>
            ":localtranslation/v4/" + ModelKey(modelId) + "/" + language + "/" + section.ToString("D5", global::System.Globalization.CultureInfo.InvariantCulture);

        public static string SourceHash(string html, string sourceLanguage) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html + "\n" + sourceLanguage)));

        // The section's cache for this source in this version (digest) of the model. A cache of another version becomes an empty one that keeps
        // the earlier texts as Stale until this version has replaced them; without a digest the cache is read as stored, whichever version wrote it.
        public static LocalTranslationCache? Parse(string json, string html, string sourceLanguage, string? digest, int count)
        {
            try
            {
                var value = JsonSerializer.Deserialize<LocalTranslationCache>(json);
                if (value is not { AlgorithmVersion: Algorithm } || value.SourceLanguage != sourceLanguage || value.SourceHash != SourceHash(html, sourceLanguage)
                    || !Bounded(value.Texts, count)) return null;
                if (value.Stale is { } stale && !Bounded(stale.Texts, count)) value = value with { Stale = null };
                if (digest is null || value.ModelDigest == digest) return value;

                string?[] earlier = [.. value.Texts.Select((text, index) => text ?? value.Stale?.Texts[index])];
                return new LocalTranslationCache(Algorithm, sourceLanguage, value.SourceHash, digest, new string?[count],
                    earlier.Any(text => text is not null) ? new LocalStaleTranslation(value.ModelDigest, earlier) : null);
            }
            catch (JsonException) { return null; }
        }

        private static bool Bounded(string?[]? texts, int count) => texts is { Length: var length } && length == count && texts.All(text => text is null || text.Length <= 20_000);

        // What a reader sees of one block: this version's text, else the earlier version's, marked stale.
        public static (string? Text, bool IsStale) Visible(LocalTranslationCache cache, int index) =>
            cache.Texts[index] is { } text ? (text, false) : cache.Stale?.Texts[index] is { } earlier ? (earlier, true) : (null, false);

        // Adds translated blocks to a section's cache under the document's row lock, so the Reader and the background job never
        // lose each other's blocks; a block already translated stays. Never called while a model runs.
        public static async Task<string?[]> StoreAsync(LogicContext ctx, long documentId, string path, string html, string sourceLanguage,
            string digest, int count, IReadOnlyDictionary<int, string> blocks, CancellationToken ct, bool replace = false)
        {
            var lockSql = new SqlStatement();
            lockSql.Statement =
                """
                SELECT document."Id" AS "DocumentId"
                FROM "ReadingDocuments" AS document
                WHERE document."Id" = @DocumentId FOR UPDATE;
                """;
            lockSql.Parameters.Add(new ReadingDocumentIdData(documentId));
            await ctx.ReadRequiredAsync<ReadingDocumentIdData>(lockSql, ct);

            var existing = await ctx.ReadOptionalAsync<ReadingTranslationCacheData>(Read(documentId, path), ct);
            var current = existing is null ? null : Parse(existing.Cache, html, sourceLanguage, digest, count);
            var texts = current?.Texts ?? new string?[count];
            // A section without text still gets its cache in this version, or the job would pick it again and again.
            var changed = count == 0 && (existing is null || Parse(existing.Cache, html, sourceLanguage, null, 0)?.ModelDigest != digest);
            foreach (var (index, value) in blocks)
            {
                if (!replace && !string.IsNullOrWhiteSpace(texts[index])) continue;
                texts[index] = value;
                changed = true;
            }
            if (changed)
            {
                // The earlier version's texts go once this version has every block.
                var stale = texts.All(text => text is not null) ? null : current?.Stale;
                var replacement = new LocalTranslationCache(Algorithm, sourceLanguage, SourceHash(html, sourceLanguage), digest, texts, stale);
                await ctx.SaveReadingResourceAsync(new ReadingResourceData(documentId, path, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replacement))), ct);
            }
            return texts;
        }

        // One section's cache as stored.
        public static SqlStatement Read(long documentId, string path)
        {
            var query = new SqlStatement();
            query.Statement =
                """
                SELECT resource."Path", convert_from(resource."Bytes", 'UTF8') AS "Cache"
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = @DocumentId AND resource."Path" = @Path;
                """;
            query.Parameters.Add(new ReadingTranslationPathData(documentId, path));
            return query;
        }
    }

    // Texts holds one entry per block of the section (null: not yet translated in this version); Stale the texts of an earlier version of the
    // model, shown until this version has replaced them.
    internal sealed record LocalTranslationCache(int AlgorithmVersion, string SourceLanguage, string SourceHash, string ModelDigest, string?[] Texts,
        LocalStaleTranslation? Stale = null);

    internal sealed record LocalStaleTranslation(string ModelDigest, string?[] Texts);

    [ServiceEndpoint("GET", "/api/v1/translation/local/availability")]
    public sealed class ReadLocalTranslationAvailabilityV1 : UserService<LocalTranslationAvailabilityParametersV1>
    {
        private readonly TranslationProviders _providers;
        internal ReadLocalTranslationAvailabilityV1(TranslationProviders providers)
        {
            _providers = providers;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationAvailabilityV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationAvailabilityParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalTranslationAvailabilityParametersV1 parameters, Type resultType) => new("profile.media.read");
        // The model id that translates now (the Owner's default, else the largest installed TranslateGemma); asking each machine is metadata work
        // and never delays opening the Reader for long.
        protected override async Task<IServiceOutput> OnRead()
        {
            var setup = await TranslationProviders.SetupAsync(Context, CancellationToken);
            return new LocalTranslationAvailabilityV1((await _providers.ChooseAsync(setup, CancellationToken))?.ModelId);
        }
    }

    // The models the Reader offers for a re-translation: those a machine translates with now, an admin-only provider's only with the admin-only right.
    [ServiceEndpoint("GET", "/api/v1/translation/local/models")]
    public sealed class ReadReaderTranslationModelsV1 : UserService<ReaderTranslationModelsParametersV1>
    {
        private readonly TranslationProviders _providers;

        internal ReadReaderTranslationModelsV1(TranslationProviders providers)
        {
            _providers = providers;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReaderTranslationModelsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReaderTranslationModelsParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(ReaderTranslationModelsParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var setup = await TranslationProviders.SetupAsync(Context, CancellationToken);
            return new ReaderTranslationModelsV1(await _providers.AvailableAsync(setup, Auth, CancellationToken));
        }
    }

    // A re-translation with a chosen model, for every reader of the book: a paragraph, the section or the whole book shows that model's text there
    // as soon as it has translated it (the Reader asks for the paragraph and the section's blocks at once, the background job for the rest).
    [ServiceEndpoint("PUT", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/choices")]
    public sealed class ChooseLocalReadingTranslationV1 : UserService<LocalReadingSectionModelParametersV1, LocalReadingChoiceDataV1>
    {
        private readonly TranslationProviders _providers;

        internal ChooseLocalReadingTranslationV1(TranslationProviders providers)
        {
            _providers = providers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingSectionModelParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingSectionModelParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            if (_providers.Find(Parameters.Model) is null) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            if (Data.Scope == ReadingTranslationScope.Block && Data.BlockIndex is null) throw new DataValidationException(nameof(Data.BlockIndex), "required");
            var setup = await TranslationProviders.SetupAsync(Context, CancellationToken);
            if (!_providers.MayChoose(Parameters.Model, setup, Auth))
                throw new SecurityException("Only the admin-only right may choose this model.");

            var data = new ReadingTranslationScopeWriteData(Parameters.DocumentId, Data.Scope == ReadingTranslationScope.Book ? null : Parameters.SectionIndex,
                Data.Scope == ReadingTranslationScope.Block ? Data.BlockIndex : null, Parameters.Language, Parameters.Model);
            if (await Context.ChooseReadingTranslationAsync(data, Auth.ProfileId!.Value, CancellationToken) == 0) throw new KeyNotFoundException();
            return new UpdateResult(true);
        }
    }

    // Reads are authorized against the canonical Reader section; draft data is never
    // added to its global translated-language or ebook catalog.
    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}")]
    public sealed class ReadLocalReadingTranslationV1 : UserService<LocalReadingTranslationParametersV1>
    {
        private readonly ReadReadingSectionV1 _sections;
        private readonly TranslationProviders _providers;

        internal ReadLocalReadingTranslationV1(ReadReadingSectionV1 sections, TranslationProviders providers)
        {
            _sections = sections;
            _providers = providers;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingTranslationParametersV1 parameters, Type resultType) => new("profile.media.read");

        // The model's texts in its current version (an earlier version's stand in, marked stale), with the blocks someone chose another model for
        // replaced by that model's text; each block says which model its text is from.
        protected override async Task<IServiceOutput> OnRead()
        {
            if (_providers.Find(Parameters.Model) is null) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var setup = await TranslationProviders.SetupAsync(Context, CancellationToken);
            var machine = await _providers.ChooseAsync(setup, CancellationToken, Parameters.Model) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            string digest;
            try { digest = await machine.Provider.DigestAsync(machine, CancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
            { throw new ServiceUnavailableException("translation_local_unavailable", exception); }

            // The blocks shown in another model's translation, for every reader of the book.
            var choiceSql = new SqlStatement();
            choiceSql.Statement =
                """
                SELECT
                    choice."BlockIndex",
                    choice."Model"
                FROM "ReadingTranslationChoices" AS choice
                WHERE choice."ReadingDocumentId" = @DocumentId
                    AND choice."SectionIndex" = @SectionIndex
                    AND choice."Language" = @Language;
                """;
            choiceSql.Parameters.Add(new ReadingTranslationSectionData(Parameters.DocumentId, Parameters.SectionIndex, Parameters.Language, []));
            var choices = (await Context.ReadSql.ReadListAsync<ReadingTranslationChoiceData>(choiceSql, CancellationToken))
                .Where(choice => choice.BlockIndex < section.Blocks && choice.Model != Parameters.Model && _providers.Find(choice.Model) is not null).ToArray();

            // The section's cache of every model involved, in one read.
            var paths = choices.Select(choice => choice.Model).Prepend(Parameters.Model).Distinct(StringComparer.Ordinal)
                .ToDictionary(model => model, model => LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, model), StringComparer.Ordinal);
            var query = new SqlStatement();
            query.Statement =
                """
                SELECT
                    resource."Path",
                    convert_from(resource."Bytes", 'UTF8') AS "Cache"
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" = ANY(@Paths)
                    AND EXISTS (
                        SELECT 1 FROM "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE document."Id" = resource."ReadingDocumentId"
                    );
                """;
            query.Parameters.Add(new ReadingTranslationSectionData(Parameters.DocumentId, Parameters.SectionIndex, Parameters.Language, [.. paths.Values]));
            query.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var caches = (await Context.ReadSql.ReadListAsync<ReadingTranslationCacheData>(query, CancellationToken)).ToDictionary(row => row.Path, row => row.Cache, StringComparer.Ordinal);

            var texts = new string?[section.Blocks];
            var models = new string?[section.Blocks];
            var stale = new bool[section.Blocks];
            if (caches.TryGetValue(paths[Parameters.Model], out var own)
                && LocalReadingPaths.Parse(own, section.Html, Parameters.SourceLanguage, digest, section.Blocks) is { } current)
            {
                for (var index = 0; index < section.Blocks; index++)
                {
                    if (LocalReadingPaths.Visible(current, index) is ({ } text, var isStale))
                        (texts[index], models[index], stale[index]) = (text, Parameters.Model, isStale);
                }
            }

            // A chosen model's text in whichever version wrote it; a block it has no text for keeps the default's.
            foreach (var group in choices.GroupBy(choice => choice.Model, StringComparer.Ordinal))
            {
                if (!caches.TryGetValue(paths[group.Key], out var json)
                    || LocalReadingPaths.Parse(json, section.Html, Parameters.SourceLanguage, null, section.Blocks) is not { } chosen) continue;
                foreach (var choice in group)
                {
                    if (LocalReadingPaths.Visible(chosen, choice.BlockIndex) is ({ } text, var isStale))
                        (texts[choice.BlockIndex], models[choice.BlockIndex], stale[choice.BlockIndex]) = (text, group.Key, isStale);
                }
            }
            return models.All(model => model is null) ? new LocalReadingTranslationV1("[]", [], []) : new LocalReadingTranslationV1(JsonSerializer.Serialize(texts), models, stale);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/blocks/{blockIndex:int}")]
    public sealed class GenerateLocalReadingBlockV1 : UserService<LocalReadingBlockParametersV1, LocalReadingBlockDataV1>
    {
        private readonly ReadReadingSectionV1 _sections;
        private readonly TranslationProviders _providers;
        private readonly LocalTranslationGate _gate;

        internal GenerateLocalReadingBlockV1(ReadReadingSectionV1 sections, TranslationProviders providers, LocalTranslationGate gate)
        {
            _sections = sections;
            _providers = providers;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingBlockV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingBlockParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingBlockParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_providers.Find(Parameters.Model) is null) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            if (!_providers.MayChoose(Parameters.Model, setup, Auth))
                throw new SecurityException("Only the admin-only right may translate with this model.");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var html = new HtmlParser().ParseDocument(section.Html);
            var nodes = html.QuerySelectorAll("[data-b]").ToArray();
            if (nodes.Length != section.Blocks || Parameters.BlockIndex >= nodes.Length)
                throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            var original = nodes[Parameters.BlockIndex];
            if (original.QuerySelector("[data-b]") is not null)
                return new LocalReadingBlockV1("");
            var source = ReadingHtml.TranslationText(original);
            if (source.Length == 0) return new LocalReadingBlockV1("");
            if (source.Length > 20_000) throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            if (!await _gate.HeavyWork.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken))
                throw new ServiceUnavailableException("translation_local_busy");

            try
            {
                // A machine that fails rests, and the block goes to the next one in the order at once; none left means unavailable.
                while (true)
                {
                    var machine = await _providers.ChooseAsync(setup, CancellationToken, Parameters.Model) ?? throw new ServiceUnavailableException("translation_local_unavailable");
                    try
                    {
                        var digest = await machine.Provider.DigestAsync(machine, CancellationToken);
                        var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);

                        // A short authorized read batch; no open SQL transaction while the model runs. Only this version's text counts: an
                        // earlier version's stays visible in the section read until it is replaced.
                        var previous = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            var row = await ctx.ReadOptionalAsync<ReadingTranslationCacheData>(LocalReadingPaths.Read(Parameters.DocumentId, path), ct);
                            return row is null ? null : LocalReadingPaths.Parse(row.Cache, section.Html, Data.SourceLanguage, digest, nodes.Length);
                        }, CancellationToken);
                        if (previous?.Texts[Parameters.BlockIndex] is { } done && !string.IsNullOrWhiteSpace(done))
                            return new LocalReadingBlockV1(done);

                        var context = ReadingHtml.TranslationContext(nodes, Parameters.BlockIndex);
                        var value = await machine.Provider.TranslateBlockAsync(machine, Data.SourceLanguage, Parameters.Language, source, context, CancellationToken)
                            ?? throw new ServiceUnavailableException("translation_local_unverified");

                        // Serialize competing writes across instances on the canonical ReadingDocument.
                        // The row lock exists even when there is no resource yet. Never lock during inference.
                        return await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            var texts = await LocalReadingPaths.StoreAsync(ctx, Parameters.DocumentId, path, section.Html, Data.SourceLanguage, digest, nodes.Length,
                                new Dictionary<int, string> { [Parameters.BlockIndex] = value }, ct);
                            return (IServiceOutput)new LocalReadingBlockV1(texts[Parameters.BlockIndex] ?? value);
                        }, CancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
                        || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
                    {
                        machine.Provider.Fail(machine);
                    }
                }
            }
            finally { _gate.HeavyWork.Release(); }
        }
    }
}
