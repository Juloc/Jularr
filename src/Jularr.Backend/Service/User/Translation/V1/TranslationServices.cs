using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Logic.Translation;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Translation.V1
{
    [ServiceEndpoint("GET", "/api/v1/translation/routes/{sourceLanguageTypeId}/{targetLanguageTypeId}")]
    public sealed class ReadTranslationRouteV1 : UserService<TranslationRouteParametersV1>
    {
        private readonly TranslationFiles _files;

        internal ReadTranslationRouteV1(TranslationFiles files)
        {
            _files = files;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<TranslationFileV1>>()];
        protected override ModuleRequirement GetInstanceModules(TranslationRouteParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(TranslationRouteParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The browser translates with these files; English bridges two other languages.
            if (!_files.IsAvailable)
            {
                throw new ServiceUnavailableException("translation_unavailable");
            }

            var route = await _files.RouteAsync(Parameters.SourceLanguageTypeId, Parameters.TargetLanguageTypeId, CancellationToken);
            return new BaseDataList<TranslationFileV1>([.. route.Select(entry => new TranslationFileV1(entry.Step, entry.File.FileType ?? "engine",
                "/api/v1/translation/files/" + TranslationModels.FileName(entry.File), entry.Precision))]);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/translation/files/{name}")]
    public sealed partial class ReadTranslationFileV1 : UserService<TranslationFileParametersV1>
    {
        private readonly TranslationFiles _files;

        internal ReadTranslationFileV1(TranslationFiles files)
        {
            _files = files;
        }

        [GeneratedRegex(@"^[0-9a-f]{64}\.[a-z0-9]{1,8}$")]
        private static partial Regex FileName();

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TranslationFileContentV1>()];
        protected override ModuleRequirement GetInstanceModules(TranslationFileParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(TranslationFileParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!FileName().IsMatch(Parameters.Name))
            {
                throw new KeyNotFoundException("Unknown translation file.");
            }

            var path = await _files.PathAsync(Parameters.Name, CancellationToken);
            return new TranslationFileContentV1(path, Parameters.Name.EndsWith(".wasm", StringComparison.Ordinal) ? "application/wasm" : "application/octet-stream");
        }
    }
}
