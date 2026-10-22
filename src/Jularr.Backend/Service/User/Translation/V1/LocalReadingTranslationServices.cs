using System.Net.Http;
using System.Text;
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
    // Stored alongside the existing section, but under a provider/model/version-specific
    // path; incomplete arrays are drafts and not listed as downloadable translations.
    internal static class LocalReadingPaths
    {
        public static string Cache(int section, string language, string model) =>
            ":translation/local-v1/" + model + "/" + language + "/" + section.ToString("D5", global::System.Globalization.CultureInfo.InvariantCulture);
    }

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

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/local-translations/{language}/{model}")]
    public sealed class ReadLocalReadingTranslationV1 : UserService<LocalReadingTranslationParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalReadingTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(LocalReadingTranslationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new KeyNotFoundException("Unknown model.");
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    convert_from(resource."Bytes", 'UTF8') AS "Blocks"
                FROM
                    "ReadingResources" AS resource
                WHERE
                    resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" = @Path
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "ReadingDocuments" AS document
                            JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            document."Id" = resource."ReadingDocumentId"
                    );
                """;
            sql.Parameters.Add(new ReadingTranslationPathData(Parameters.DocumentId, LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model)));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingTranslationV1>(sql, CancellationToken) ?? new ReadingTranslationV1("[]");
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
            var parser = new HtmlParser();
            var html = parser.ParseDocument(section.Html);
            var nodes = html.QuerySelectorAll("[data-b]");
            if (nodes.Length != section.Blocks || Parameters.BlockIndex >= nodes.Length)
                throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            var original = nodes[Parameters.BlockIndex];
            if (original.QuerySelector("[data-b]") is not null || string.IsNullOrWhiteSpace(original.TextContent))
                return new LocalReadingBlockV1("");
            var source = original.TextContent.Trim();
            if (source.Length > 2400) throw new DataValidationException(nameof(Parameters.BlockIndex), "translation_mismatch");

            if (!await _gate.HeavyWork.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken))
                throw new ServiceUnavailableException("translation_local_busy");
            try
            {
                var installed = await _models.InstalledAsync(server, CancellationToken);
                if (!installed.Contains(Parameters.Model, StringComparer.Ordinal))
                    throw new ServiceUnavailableException("translation_local_not_installed");

                // Check and update only within the one HeavyWork lock to avoid lost updates.
                var path = LocalReadingPaths.Cache(Parameters.SectionIndex, Parameters.Language, Parameters.Model);
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        convert_from(resource."Bytes", 'UTF8') AS "Blocks"
                    FROM
                        "ReadingResources" AS resource
                    WHERE
                        resource."ReadingDocumentId" = @DocumentId
                        AND resource."Path" = @Path;
                    """;
                sql.Parameters.Add(new ReadingTranslationPathData(Parameters.DocumentId, path));
                var existing = await Context.Sql.ReadOptionalAsync<ReadingTranslationV1>(sql, CancellationToken);
                var texts = existing is { } cached ? ReadingTranslations.Parse(cached.Blocks)?.ToArray() : null;
                if (texts is null || texts.Length != nodes.Length) texts = new string[nodes.Length];
                var current = texts[Parameters.BlockIndex];
                if (!string.IsNullOrWhiteSpace(current)) return new LocalReadingBlockV1(current);

                var translated = await _models.TranslateAsync(server, Parameters.Model, Data.SourceLanguage, Parameters.Language, source, CancellationToken);
                var value = translated.Text;
                // Deterministic completion checks cannot guarantee semantic correctness.
                if (value.Length < Math.Min(3, source.Length / 12) || value.Length > source.Length * 6 + 200
                    || value.Equals(source, StringComparison.Ordinal) && Data.SourceLanguage != Parameters.Language)
                    throw new ServiceUnavailableException("translation_local_unverified");
                texts[Parameters.BlockIndex] = value;
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.SaveReadingResourceAsync(new ReadingResourceData(Parameters.DocumentId, path, "application/json",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(texts))), ct);
                    return true;
                }, CancellationToken);
                return new LocalReadingBlockV1(value);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException)
            {
                throw new ServiceUnavailableException("translation_local_unavailable", e);
            }
            finally { _gate.HeavyWork.Release(); }
        }
    }
}
