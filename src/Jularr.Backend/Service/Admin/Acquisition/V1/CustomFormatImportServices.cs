using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Servarr;
using Jularr.Integrations.Logic.Servarr;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/custom-formats/import")]
    public sealed class ImportCustomFormatsV1 : AdminService<SaveCustomFormatParametersV1, ImportCustomFormatsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SavedCustomFormatV1>>()];
        protected override ModuleRequirement GetInstanceModules(SaveCustomFormatParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SaveCustomFormatParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            if (Data.TakeoverSourceTypeId is not (TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr)) throw new DataValidationException(nameof(Data.TakeoverSourceTypeId), "unknown_catalog_value");
            try
            {
                using var document = JsonDocument.Parse(Data.Json);
                var catalog = ServarrFormatCatalog.Portable(Data.TakeoverSourceTypeId == TakeoverSourceType.Radarr);
                var parsed = ServarrApiClient.ParseCustomFormats(document.RootElement);
                if (parsed.Count == 0) throw new DataValidationException(nameof(Data.Json), "required");
                var mapped = parsed.Select(format => ServarrFormatMapping.Map(format, catalog, Data.MediaTypeId)).ToArray();
                var result = await Context.ImportCustomFormatsAsync(Data.MediaTypeId, mapped, CancellationToken);
                return new BaseDataList<SavedCustomFormatV1>(result.Select(format => new SavedCustomFormatV1(format.Id, format.Version)));
            }
            catch (JsonException)
            {
                throw new DataValidationException(nameof(Data.Json), "invalid_type");
            }
            catch (ServarrException)
            {
                throw new DataValidationException(nameof(Data.Json), "invalid_type");
            }
            catch (DataValidationException exception)
            {
                throw new DataValidationException(nameof(Data.Json), exception.Code);
            }
        }
    }
}
