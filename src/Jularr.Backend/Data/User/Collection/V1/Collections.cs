using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Collection.V1
{
    public sealed class CollectionsParametersV1 : BaseParameters
    {
    }

    public sealed class CollectionParametersV1 : BaseParameters
    {
        public CollectionParametersV1(long collectionId)
        {
            CollectionId = collectionId;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }
    }

    public sealed class CollectionWorksParametersV1 : BaseParameters
    {
        public CollectionWorksParametersV1(long collectionId, int page = 1, int pageSize = 60)
        {
            CollectionId = collectionId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class CollectionWorkParametersV1 : BaseParameters
    {
        public CollectionWorkParametersV1(long collectionId, long workId)
        {
            CollectionId = collectionId;
            WorkId = workId;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class CreateCollectionDataV1 : BaseData
    {
        public CreateCollectionDataV1(string name, bool isSmart)
        {
            Name = name;
            IsSmart = isSmart;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Name))]
        public string Name { get; }

        // A smart collection is a saved filter; the kind never changes.
        [DataField(typeof(CollectionFields), nameof(CollectionFields.IsSmart))]
        public bool IsSmart { get; }
    }

    // Filters apply to smart collections only.
    public sealed class UpdateCollectionDataV1 : BaseData
    {
        public UpdateCollectionDataV1(string name, long version, MediaType? mediaTypeId = null, MediaSubtype? mediaSubtypeId = null, int? releaseYearFrom = null, int? releaseYearTo = null,
            bool onlyAvailable = false, bool onlyUnfinished = false)
        {
            Name = name;
            Version = version;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            ReleaseYearFrom = releaseYearFrom;
            ReleaseYearTo = releaseYearTo;
            OnlyAvailable = onlyAvailable;
            OnlyUnfinished = onlyUnfinished;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Version))]
        public long Version { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.MediaTypeId))]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.ReleaseYear))]
        public int? ReleaseYearFrom { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.ReleaseYear))]
        public int? ReleaseYearTo { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.OnlyAvailable))]
        public bool OnlyAvailable { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.OnlyUnfinished))]
        public bool OnlyUnfinished { get; }

        public override void Validate()
        {
            base.Validate();
            if (ReleaseYearFrom > ReleaseYearTo)
            {
                throw new DataValidationException(nameof(ReleaseYearTo), "out_of_range");
            }
        }
    }

    public sealed class CollectionV1 : BaseResponse
    {
        public CollectionV1(long id, string name, bool isSmart, MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId, int? releaseYearFrom, int? releaseYearTo, bool onlyAvailable,
            bool onlyUnfinished, int? workCount, long version)
        {
            Id = id;
            Name = name;
            IsSmart = isSmart;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            ReleaseYearFrom = releaseYearFrom;
            ReleaseYearTo = releaseYearTo;
            OnlyAvailable = onlyAvailable;
            OnlyUnfinished = onlyUnfinished;
            WorkCount = workCount;
            Version = version;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long Id { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.IsSmart))]
        public bool IsSmart { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.MediaTypeId))]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.ReleaseYear))]
        public int? ReleaseYearFrom { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.ReleaseYear))]
        public int? ReleaseYearTo { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.OnlyAvailable))]
        public bool OnlyAvailable { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.OnlyUnfinished))]
        public bool OnlyUnfinished { get; }

        // Hand-picked collections only; a filter is counted when it is shown.
        [DataField(typeof(CollectionFields), nameof(CollectionFields.WorkCount))]
        public int? WorkCount { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Version))]
        public long Version { get; }
    }

    public sealed class WorkCollectionV1 : BaseResponse
    {
        public WorkCollectionV1(long collectionId, string name, bool containsWork)
        {
            CollectionId = collectionId;
            Name = name;
            ContainsWork = containsWork;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Name))]
        public string Name { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.ContainsWork))]
        public bool ContainsWork { get; }
    }

    public sealed class CreateCollectionResultV1 : BaseResponse
    {
        public CreateCollectionResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long Id { get; }
    }
}
