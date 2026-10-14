using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class CollectionSaveData : BaseData
    {
        public CollectionSaveData(long profileId, long? collectionId, string name, bool isSmart, MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId, int? releaseYearFrom, int? releaseYearTo,
            bool onlyAvailable, bool onlyUnfinished, long? version)
        {
            ProfileId = profileId;
            CollectionId = collectionId;
            Name = name;
            IsSmart = isSmart;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            ReleaseYearFrom = releaseYearFrom;
            ReleaseYearTo = releaseYearTo;
            OnlyAvailable = onlyAvailable;
            OnlyUnfinished = onlyUnfinished;
            Version = version;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id), false)]
        public long? CollectionId { get; }

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

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class CollectionWorkData : BaseData
    {
        public CollectionWorkData(long profileId, long collectionId, long workId)
        {
            ProfileId = profileId;
            CollectionId = collectionId;
            WorkId = workId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class CollectionStateData : BaseData
    {
        public CollectionStateData(bool isSmart, long version)
        {
            IsSmart = isSmart;
            Version = version;
        }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.IsSmart))]
        public bool IsSmart { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Version))]
        public long Version { get; }
    }

    internal sealed class CollectionOwnerData : BaseData
    {
        public CollectionOwnerData(long profileId, long collectionId)
        {
            ProfileId = profileId;
            CollectionId = collectionId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(CollectionFields), nameof(CollectionFields.Id))]
        public long CollectionId { get; }
    }
}
