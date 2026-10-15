using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class TakeoverParametersV1 : BaseParameters
    {
    }

    public sealed class RunTakeoverDataV1 : BaseData
    {
        public RunTakeoverDataV1(TakeoverSourceType takeoverSourceTypeId, string baseUrl, string apiKey, bool apply, string[]? rootFolders = null)
        {
            TakeoverSourceTypeId = takeoverSourceTypeId;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            Apply = apply;
            RootFolders = rootFolders;
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
