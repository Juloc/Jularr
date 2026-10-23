using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Translation.V1
{
    // Provider-specific cache: intentionally outside the canonical :translation/{language} paths.
    internal static class LocalReadingPaths
    {
        public const int Algorithm = 2;
        public static string Cache(int section, string language, string model) =>
            ":localtranslation/v2/" + model + "/" + language + "/" + section.ToString("D5", global::System.Globalization.CultureInfo.InvariantCulture);

        public static string SourceHash(string html, string sourceLanguage) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html + "\n" + sourceLanguage)));

        public static LocalTranslationCache? Parse(string json, string html, string sourceLanguage, string digest, int count)
        {
            try
            {
                var value = JsonSerializer.Deserialize<LocalTranslationCache>(json);
                return value is { AlgorithmVersion: Algorithm } && value.SourceLanguage == sourceLanguage
                    && value.SourceHash == SourceHash(html, sourceLanguage) && value.ModelDigest == digest
                    && value.Texts is { Length: var length } && length == count
                    && value.Texts.All(text => text is null || text.Length <= 20_000) ? value : null;
            }
            catch (JsonException) { return null; }
        }
    }

    internal sealed record LocalTranslationCache(int AlgorithmVersion, string SourceLanguage, string SourceHash, string ModelDigest, string[] Texts);

    [ServiceEndpoint("GET", "/api/v1/translation/local/availability")]
    public sealed class ReadLocalTranslationAvailabilityV1 : UserService<LocalTranslationAvailabilityParametersV1>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;
        internal ReadLocalTranslationAvailabilityV1(OllamaLocalModels models, IOptions<LocalTranslationOptions> options)
        {
            _models = models;
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationAvailabilityV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationAvailabilityParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalTranslationAvailabilityParametersV1 parameters, Type resultType) => new("instance.settings.update");
        protected override async Task<IServiceOutput> OnRead()
        {
            var server = OllamaLocalModels.Endpoint(_options.Endpoint);
            if (server is null) return new LocalTranslationAvailabilityV1(null);
            try
            {
                var installed = await _models.InstalledAsync(server, CancellationToken);
                return new LocalTranslationAvailabilityV1(OllamaLocalModels.Candidates.FirstOrDefault(candidate => installed.Contains(candidate, StringComparer.Ordinal)));
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                return new LocalTranslationAvailabilityV1(null);
            }
        }
    }

    // Reads are authorized against the canonical Reader section; draft data is never
    // added to its global translated-language or ebook catalog.
    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}")]
    public sealed class ReadLocalReadingTranslationV1 : UserService<LocalReadingTranslationParametersV1>
    {
        private readonly ReadReadingSectionV1 _sections;
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;

        internal ReadLocalReadingTranslationV1(ReadReadingSectionV1 sections, OllamaLocalModels models, IOptions<LocalTranslationOptions> options)
        {
            _sections = sections;
            _models = models;
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingTranslationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var server = OllamaLocalModels.Endpoint(_options.Endpoint) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            string digest;
            try { digest = await _models.DigestAsync(server, Parameters.Model, CancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
            { throw new ServiceUnavailableException("translation_local_unavailable", exception); }

            var query = new SqlStatement();
            query.Statement =
                """
                SELECT convert_from(resource."Bytes", 'UTF8') AS "Blocks"
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" = @Path
                    AND EXISTS (
                        SELECT 1 FROM "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE document."Id" = resource."ReadingDocumentId"
                    );
                """;
            query.Parameters.Add(new ReadingTranslationPathData(Parameters.DocumentId, LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model)));
            query.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var stored = await Context.ReadSql.ReadOptionalAsync<ReadingTranslationV1>(query, CancellationToken);
            var parsed = stored is null ? null : LocalReadingPaths.Parse(stored.Blocks, section.Html, Parameters.SourceLanguage, digest, section.Blocks);
            return new ReadingTranslationV1(JsonSerializer.Serialize(parsed?.Texts ?? []));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}/blocks/{blockIndex:int}")]
    public sealed class GenerateLocalReadingBlockV1 : UserService<LocalReadingBlockParametersV1, LocalReadingBlockDataV1>
    {
        private readonly ReadReadingSectionV1 _sections;
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;
        private readonly LocalTranslationGate _gate;

        internal GenerateLocalReadingBlockV1(ReadReadingSectionV1 sections, OllamaLocalModels models,
            IOptions<LocalTranslationOptions> options, LocalTranslationGate gate)
        {
            _sections = sections;
            _models = models;
            _options = options.Value;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalReadingBlockV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingBlockParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingBlockParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var server = OllamaLocalModels.Endpoint(_options.Endpoint) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            var section = await _sections.ExecuteAsync<ReadingSectionV1>(new ReadingSectionParametersV1(Parameters.DocumentId, Parameters.SectionIndex), CancellationToken);
            var html = new HtmlParser().ParseDocument(section.Html);
            var nodes = html.QuerySelectorAll("[data-b]");
            if (nodes.Length != section.Blocks || Parameters.BlockIndex >= nodes.Length)
                throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            var original = nodes[Parameters.BlockIndex];
            if (original.QuerySelector("[data-b]") is not null || string.IsNullOrWhiteSpace(original.TextContent))
                return new LocalReadingBlockV1("");
            var source = original.TextContent.Trim();
            if (source.Length > 20_000) throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            if (!await _gate.HeavyWork.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken))
                throw new ServiceUnavailableException("translation_local_busy");

            try
            {
                var digest = await _models.DigestAsync(server, Parameters.Model, CancellationToken);
                var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);
                var query = new SqlStatement();
                query.Statement =
                    """
                    SELECT convert_from(resource."Bytes", 'UTF8') AS "Blocks"
                    FROM "ReadingResources" AS resource
                    WHERE resource."ReadingDocumentId" = @DocumentId AND resource."Path" = @Path;
                    """;
                query.Parameters.Add(new ReadingTranslationPathData(Parameters.DocumentId, path));

                // A short authorized read batch; no open SQL transaction while the model runs.
                var previous = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var row = await ctx.ReadOptionalAsync<ReadingTranslationV1>(query, ct);
                    return row is null ? null : LocalReadingPaths.Parse(row.Blocks, section.Html, Data.SourceLanguage, digest, nodes.Length);
                }, CancellationToken);
                if (!string.IsNullOrWhiteSpace(previous?.Texts[Parameters.BlockIndex]))
                    return new LocalReadingBlockV1(previous.Texts[Parameters.BlockIndex]);

                var translated = await _models.TranslateAsync(server, Parameters.Model, Data.SourceLanguage, Parameters.Language, source, CancellationToken);
                var value = translated.Text;
                // Do not silently accept truncation, identical language or absurd length.
                if (value.Length < Math.Min(3, source.Length / 12) || value.Length > 20_000 || value.Length > source.Length * 6 + 200)
                    throw new ServiceUnavailableException("translation_local_unverified");

                // Serialize competing writes across instances on the canonical ReadingDocument.
                // The row lock exists even when there is no resource yet. Never lock during inference.
                return await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var lockSql = new SqlStatement();
                    lockSql.Statement =
                        """
                        SELECT document."Id" AS "DocumentId"
                        FROM "ReadingDocuments" AS document
                        WHERE document."Id" = @DocumentId FOR UPDATE;
                        """;
                    lockSql.Parameters.Add(new ReadingDocumentIdData(Parameters.DocumentId));
                    await ctx.ReadRequiredAsync<ReadingDocumentIdData>(lockSql, ct);

                    var existing = await ctx.ReadOptionalAsync<ReadingTranslationV1>(query, ct);
                    var cache = existing is null ? null : LocalReadingPaths.Parse(existing.Blocks, section.Html, Data.SourceLanguage, digest, nodes.Length);
                    var texts = cache?.Texts ?? new string[nodes.Length];
                    if (string.IsNullOrWhiteSpace(texts[Parameters.BlockIndex]))
                    {
                        texts[Parameters.BlockIndex] = value;
                        var replacement = new LocalTranslationCache(LocalReadingPaths.Algorithm, Data.SourceLanguage,
                            LocalReadingPaths.SourceHash(section.Html, Data.SourceLanguage), digest, texts);
                        await ctx.SaveReadingResourceAsync(new ReadingResourceData(Parameters.DocumentId, path,
                            "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replacement))), ct);
                    }
                    return (IServiceOutput)new LocalReadingBlockV1(texts[Parameters.BlockIndex]);
                }, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                throw new ServiceUnavailableException("translation_local_unavailable", exception);
            }
            finally { _gate.HeavyWork.Release(); }
        }
    }
}
