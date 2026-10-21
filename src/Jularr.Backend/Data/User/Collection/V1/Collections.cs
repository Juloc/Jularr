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
        public CollectionWorksParametersV1(long collectionId, int page = 1, int pageSize = 60, MediaType[]? mediaTypeIds = null, MediaSubtype[]? mediaSubtypeIds = null, int? releaseYearFrom = null,
            int? releaseYearTo = null, ProgressStateType? progressStateId = null, LibrarySortType? librarySortTypeId = null)
        {
            ProgressStateId = progressStateId;
            CollectionId = collectionId;
            Page = page;
            PageSize = pageSize;
            MediaTypeIds = mediaTypeIds ?? [];
            MediaSubtypeIds = mediaSubtypeIds ?? [];
            ReleaseYearFrom = releaseYearFrom;
            ReleaseYearTo = releaseYearTo;
            LibrarySortTypeId = librarySortTypeId;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }

        public int Page { get; }
        public int PageSize { get; }

        // Narrows the collection further: a type lists its Works without subtype, a subtype lists that subtype across types; none lists everything.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYearFrom { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYearTo { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ProgressStateId), false)]
        public ProgressStateType? ProgressStateId { get; }

        // Without a choice a hand-picked collection keeps the order its titles were added in; recently added lists the newest first.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId), false)]
        public LibrarySortType? LibrarySortTypeId { get; }
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
            bool onlyUnfinished, int? workCount, string[] posterUrls, long version)
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
            PosterUrls = posterUrls;
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

        // The covers of the first visible titles of a hand-picked collection, as the list shows them; none for a filter.
        [DataField(typeof(CollectionFields), nameof(CollectionFields.PosterUrls))]
        public string[] PosterUrls { get; }

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

    public sealed class CollectionMembershipParametersV1 : BaseParameters
    {
        public CollectionMembershipParametersV1(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Ids))]
        public long[] WorkIds { get; }
    }

    // One title in one hand-picked collection of the profile.
    public sealed class CollectionMemberV1 : BaseResponse
    {
        public CollectionMemberV1(long workId, long collectionId)
        {
            WorkId = workId;
            CollectionId = collectionId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }
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
