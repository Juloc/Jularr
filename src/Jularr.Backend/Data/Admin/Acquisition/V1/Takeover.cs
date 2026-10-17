using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class TakeoverParametersV1 : BaseParameters
    {
    }

    public sealed class RunTakeoverDataV1 : BaseData
    {
        public RunTakeoverDataV1(TakeoverSourceType takeoverSourceTypeId, string baseUrl, string apiKey, bool apply, string[]? rootFolders = null,
            BaseDataList<TakeoverConnectionKeyV1>? connectionKeys = null, BaseDataList<TakeoverLibraryV1>? newLibraries = null)
        {
            TakeoverSourceTypeId = takeoverSourceTypeId;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            Apply = apply;
            RootFolders = rootFolders;
            ConnectionKeys = connectionKeys ?? new([]);
            NewLibraries = newLibraries ?? new([]);
        }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.TakeoverSourceTypeId))]
        public TakeoverSourceType TakeoverSourceTypeId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }

        // Used for this read only; never stored.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ApiKey), true)]
        public string ApiKey { get; }

        // False shows what would happen.
        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.Apply))]
        public bool Apply { get; }

        // The libraries chosen for its root folders; titles in others take the default library.
        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.RootFolders), false)]
        public string[]? RootFolders { get; }

        public BaseDataList<TakeoverConnectionKeyV1> ConnectionKeys { get; }
        public BaseDataList<TakeoverLibraryV1> NewLibraries { get; }

        public override void Validate()
        {
            base.Validate();
            if (ConnectionKeys.Count > 100 || NewLibraries.Count > 50) throw new DataValidationException(nameof(ConnectionKeys), "out_of_range");
            ConnectionKeys.Validate(nameof(ConnectionKeys));
            NewLibraries.Validate(nameof(NewLibraries));
        }
    }

    public sealed class TakeoverConnectionKeyV1 : BaseData
    {
        public TakeoverConnectionKeyV1(TakeoverItemType takeoverItemTypeId, string baseUrl, string? apiKey)
        {
            TakeoverItemTypeId = takeoverItemTypeId;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
        }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.TakeoverItemTypeId))]
        public TakeoverItemType TakeoverItemTypeId { get; }
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; }
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.ApiKey), false)]
        public string? ApiKey { get; }
    }

    public sealed class TakeoverLibraryV1 : BaseData
    {
        public TakeoverLibraryV1(string sourcePath, string name, long? storageMountId, string relativePath)
        {
            SourcePath = sourcePath;
            Name = name;
            StorageMountId = storageMountId;
            RelativePath = relativePath;
        }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.Name))]
        public string SourcePath { get; }
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }
        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? StorageMountId { get; }
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
    }

    public sealed class TakeoverItemV1 : BaseResponse
    {
        public TakeoverItemV1(TakeoverItemType takeoverItemTypeId, string name, string outcomeKey, string? suggestedUrl)
        {
            TakeoverItemTypeId = takeoverItemTypeId;
            Name = name;
            OutcomeKey = outcomeKey;
            SuggestedUrl = suggestedUrl;
        }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.TakeoverItemTypeId))]
        public TakeoverItemType TakeoverItemTypeId { get; }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.Name))]
        public string Name { get; }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.OutcomeKey))]
        public string OutcomeKey { get; }

        // A connection that still needs its key is completed in the connection form.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), false)]
        public string? SuggestedUrl { get; }

        // A library a root folder can go to.
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; init; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name), false)]
        public string? LibraryName { get; init; }
    }
}
